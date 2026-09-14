using System.Diagnostics;

namespace CutTimer.Core;

/// <summary>
/// 追踪核心。
///
/// 关键设计（实测验证后确定）：
///   1. CSP 只在**保存**时把作业时间写回 .clip（编辑过程中不写盘），
///      所以本类只监视 mtime/size 变化，变化即视为「刚保存」，才去读文件。
///   2. 读到的绝对值包含**上游作业者**的时间，所以不能直接用。
///      做法是记录首次见到的值当 baseline，之后只累计**正向增量**——
///      上游那部分是个常量偏移，做差分就自动抵消了。
///   3. 增量只在文件于 CSP 中打开并操作时增长，所以它天然排除了挂机、
///      查参考、回消息的时间，即「净作画时长」。
/// </summary>
public sealed class CutTracker : IDisposable
{
    /// <summary>这些进程在前台时，才认为你在作画。</summary>
    public static readonly string[] PaintProcesses =
    {
        "CLIPStudioPaint",   // CLIP STUDIO PAINT（主力）
        "PaintMan", "Stylos", "TraceMan", "CoreRETAS",   // RETAS
        "sai2", "krita",
    };

    private readonly StateStore _store;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _fileTimer;
    private readonly System.Threading.Timer _tickTimer;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Stopwatch _lastPersist = Stopwatch.StartNew();

    /// <summary>连续读取失败计数，用于「文件还在写入」时限次重试，避免死循环。</summary>
    private readonly Dictionary<string, int> _readFailures = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxReadRetries = 8;   // 8 次 × 2 秒 ≈ 16 秒，足够 CSP 写完一个大文件

    /// <summary>监视到某个 .clip 被保存（mtime 变化）时触发，参数为绝对路径。</summary>
    public event Action<string>? ClipSaved;

    public AppState State { get; private set; }
    public TimeSpan SessionTime { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastReadAt { get; private set; }

    /// <summary>当前前台的作画软件进程名；不在作画时为 null。用于界面显示对应图标。</summary>
    public string? ForegroundPaintApp { get; private set; }

    /// <summary>本次计时是否被手动暂停（只在内存里，不跨重启）。</summary>
    public bool SessionPaused { get; set; }

    /// <summary>把「本次」计时清零。</summary>
    public void ResetSession()
    {
        SessionTime = TimeSpan.Zero;
        Changed?.Invoke();
    }

    /// <summary>状态有变化时触发（可能在非 UI 线程，订阅方自行调度）。</summary>
    public event Action? Changed;

    public CutTracker(StateStore store)
    {
        _store = store;
        State = store.Load();
        ApplyFirstRunDefaults();
        if (RepairCurrent()) { try { store.Save(State); } catch { /* 忽略 */ } }
        // 文件变化轮询：2 秒足够（保存不是高频事件），作为 watcher 的兜底
        _fileTimer = new System.Threading.Timer(_ => PollFiles(), null,
            Timeout.Infinite, Timeout.Infinite);
        // 前台/空闲轮询：1 秒，用于「本次」实时计时
        _tickTimer = new System.Threading.Timer(_ => Tick(), null,
            Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// 当前卡若指向一条不存在的记录（记录被删、或 state.json 被手工改过），
    /// 就退回到最近活动的那张。否则界面会一直卡在「等待中…」，看着像坏了。
    /// 返回是否真的修过（修过才需要写盘）。
    /// </summary>
    private bool RepairCurrent()
    {
        if (State.Current is not null && State.Cuts.ContainsKey(State.Current)) return false;

        State.Current = State.Cuts
            .Where(kv => !kv.Value.Archived)
            .OrderByDescending(kv => kv.Value.LastActive)
            .Select(kv => kv.Key)
            .FirstOrDefault();
        return true;
    }

    /// <summary>首次运行时给两个最常见的目录当默认值，省得用户一上来面对空白。</summary>
    private void ApplyFirstRunDefaults()
    {
        if (State.WatchRoots.Count > 0) return;
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                 })
        {
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                State.WatchRoots.Add(folder);
        }
    }

    public CutState? Current
        => State.Current is not null && State.Cuts.TryGetValue(State.Current, out var c) ? c : null;

    public string? CurrentPath => State.Current;

    public void Start()
    {
        try { File.WriteAllText(Path.Combine(_store.Root, "debug.log"), ""); } catch { }   // 每次启动清空
        Log("Start");
        _fileTimer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        _tickTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        SetupWatchers();

        // 注意：这里**不做**任何磁盘扫描。
        // 启动时遍历磁盘找 .clip 会毫无预告地吃掉几十秒 IO，让人以为程序卡住了，
        // 而且对一个「只是计时」的工具来说动静太大。
        // 想找已有工作目录请到设置页点「扫描磁盘」—— 见 ScanWorkspaceNow()。
        // 跟着 CSP 走的那条自动发现不受影响（它只读一个小 SQLite，不碰磁盘遍历）。
    }

    /// <summary>
    /// 用户手动触发的全盘扫描：找出含近期 .clip 的目录并纳入监视。
    /// 返回新增的监视根数量。<paramref name="progress"/> 用来把「已检查多少、找到多少」回显到界面。
    /// </summary>
    public int ScanWorkspaceNow(IProgress<ScanProgress>? progress = null)
    {
        void Say(string m)
        {
            Log(m);
            int v = _lastScanVisited;
            int h = _lastScanHits;
            progress?.Report(new ScanProgress(v, h, m));
        }

        try
        {
            Say("正在扫描磁盘…");
            List<string> dirs = WorkspaceScanner.FindRecentClipFolders(
                TimeSpan.FromSeconds(40),
                log: m => { Log(m); },
                progress: new Progress<ScanProgress>(p =>
                {
                    _lastScanVisited = p.VisitedDirs;
                    _lastScanHits = p.HitFolders;
                    progress?.Report(p);
                }));

            // 折叠：一部作品下的几百个子目录合并成上层，免得建几百个 watcher
            List<string> roots = WorkspaceScanner.Collapse(dirs);
            progress?.Report(new ScanProgress(_lastScanVisited, dirs.Count,
                $"已找到 {dirs.Count} 个目录，正在合并…"));

            var toAdd = new List<string>();
            lock (_gate)
            {
                foreach (string dir in roots)
                {
                    bool covered = State.WatchRoots.Any(r =>
                        dir.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                        dir.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
                    if (!covered) toAdd.Add(dir);
                }
                foreach (string d in toAdd) State.WatchRoots.Add(d);
                State.WorkspaceScanDone = true;
            }

            if (toAdd.Count > 0)
            {
                Log($"  + {toAdd.Count} 个新监视目录");
                Persist();
                SetupWatchers();
                Changed?.Invoke();
                progress?.Report(new ScanProgress(_lastScanVisited, dirs.Count,
                    $"完成：{dirs.Count} 个目录合并为 {roots.Count} 个监视根，新增 {toAdd.Count} 个"));
            }
            else
            {
                Persist();
                progress?.Report(new ScanProgress(_lastScanVisited, dirs.Count, "完成：没有发现新的目录"));
            }
            return toAdd.Count;
        }
        catch (Exception ex)
        {
            Log($"workspace scan FAILED: {ex.Message}");
            progress?.Report(new ScanProgress(_lastScanVisited, _lastScanHits, "扫描失败：" + ex.Message));
            return 0;
        }
    }

    private int _lastScanVisited;
    private int _lastScanHits;

    public void Stop()
    {
        _fileTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _tickTimer.Change(Timeout.Infinite, Timeout.Infinite);
        TearDownWatchers();
    }

    // ---------------- 目录监视（自动发现你在画的卡） ----------------

    /// <summary>
    /// 盯住工作目录，任何 .clip 被保存就自动登记并设为当前卡。
    /// 这是「不用手动添加」的实现方式 —— 只登记你真正保存过的文件。
    ///
    /// 不用轮询而是 FileSystemWatcher：目录里可能有成百上千个 .clip，
    /// 但我们只关心「谁刚刚被写过」，交给操作系统的变更通知最省。
    /// </summary>
    /// <summary>监视目录变化后重建 watcher。</summary>
    public void RestartWatchers()
    {
        SetupWatchers();
        Changed?.Invoke();
    }

    private void Log(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(_store.Root, "debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    private readonly object _watcherGate = new();
    private DateTime _lastWorkspaceCheck = DateTime.MinValue;

    private void SetupWatchers()
    {
        lock (_watcherGate)
        {
            TearDownWatchersLocked();
            Log($"SetupWatchers: roots=[{string.Join(", ", State.WatchRoots)}]");

            foreach (string root in State.WatchRoots)
            {
                if (!Directory.Exists(root)) { Log($"  skip (not found): {root}"); continue; }
                try
                {
                    var w = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                        Filter = "*.clip",
                        InternalBufferSize = 64 * 1024,
                    };
                    w.Changed += OnWatchedChange;
                    w.Created += OnWatchedChange;
                    w.Renamed += OnWatchedRename;
                    w.Error += (_, e) => Log($"  watcher error: {e.GetException().Message}");
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                    Log($"  watching: {root}");
                }
                catch (Exception ex)
                {
                    Log($"  FAILED {root}: {ex.Message}");
                }
            }
        }
    }

    private void TearDownWatchers()
    {
        lock (_watcherGate) TearDownWatchersLocked();
    }

    private void TearDownWatchersLocked()
    {
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        }
        _watchers.Clear();
    }

    private void OnWatchedChange(object sender, FileSystemEventArgs e)
    {
        try
        {
            string full = Path.GetFullPath(e.FullPath);
            Log($"event {e.ChangeType}: {full}");

            bool isNew;
            lock (_gate)
            {
                isNew = !State.Cuts.ContainsKey(full);
                if (isNew)
                {
                    var st = new CutState { Display = Path.GetFileNameWithoutExtension(full) };

                    // 读作业时间。文件可能还在写入（CSP 保存大文件时会先独占再写完），
                    // 这时读会抛 IOException —— 不要因此放弃登记，基线留 0，
                    // 交给 2 秒轮询的 HarvestOne 稍后补齐（它有 LastSeen==0 的初始化分支）。
                    try
                    {
                        ClipReading r = ClipWorkTime.Read(full);
                        if (r.Ok) { st.Baseline = r.WorkTimeMs; st.LastSeen = r.WorkTimeMs; }
                    }
                    catch (IOException)
                    {
                        Log("  busy (still writing) — 先登记，基线待轮询补齐");
                    }

                    st.FirstSeen = DateTimeOffset.Now;
                    st.LastActive = st.FirstSeen;
                    TouchFileStats(full, st);
                    State.Cuts[full] = st;

                    // 刚保存的卡就是你在画的那张
                    if (State.AutoSwitchBySave) State.Current = full;

                    Persist();
                    Log($"  registered: {st.Display} baseline={st.Baseline}");
                }
            }

            HarvestOne(full);
            if (isNew) ClipSaved?.Invoke(full);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log($"event handler FAILED: {ex}");
        }
    }

    private void OnWatchedRename(object sender, RenamedEventArgs e)
    {
        if (e.FullPath.EndsWith(".clip", StringComparison.OrdinalIgnoreCase))
            OnWatchedChange(sender, new FileSystemEventArgs(WatcherChangeTypes.Changed,
                Path.GetDirectoryName(e.FullPath) ?? "", Path.GetFileName(e.FullPath)));
    }

    // ---------------- 注册 / 切换 ----------------

    /// <summary>
    /// 登记一张卡。首次登记时把当前文件值记为基线（不计入"你的时间"），
    /// 这正是「接手别人做过的卡」的正确语义。
    /// </summary>
    public CutState Register(string path, bool makeCurrent = false)
    {
        string full = Path.GetFullPath(path);
        CutState st;
        lock (_gate)
        {
            if (!State.Cuts.TryGetValue(full, out var existing))
            {
                st = new CutState { Display = Path.GetFileNameWithoutExtension(full) };
                ClipReading r = ClipWorkTime.Read(full);
                if (r.Ok)
                {
                    st.Baseline = r.WorkTimeMs;
                    st.LastSeen = r.WorkTimeMs;
                }
                st.FirstSeen = DateTimeOffset.Now;
                st.LastActive = st.FirstSeen;
                TouchFileStats(full, st);
                State.Cuts[full] = st;
            }
            else
            {
                st = existing;
            }

            if (makeCurrent || State.Current is null)
                State.Current = full;

            Persist();
        }
        Changed?.Invoke();
        return st;
    }

    public void SetCurrent(string? path)
    {
        lock (_gate)
        {
            State.Current = path is null ? null : Path.GetFullPath(path);
            if (Current is not null) Current.LastActive = DateTimeOffset.Now;
            SessionTime = TimeSpan.Zero;   // 切卡重置「本次」
            Persist();
        }
        Changed?.Invoke();
    }

    /// <summary>把当前文件值设为新基线 —— 相当于「从现在起才算我的」。</summary>
    public void ResetBaseline(string path)
    {
        string full = Path.GetFullPath(path);
        lock (_gate)
        {
            if (!State.Cuts.TryGetValue(full, out var st)) return;
            ClipReading r = ClipWorkTime.Read(full);
            if (r.Ok)
            {
                st.Baseline = r.WorkTimeMs;
                st.LastSeen = r.WorkTimeMs;
            }
            st.Mine = 0;
            TouchFileStats(full, st);
            Persist();
        }
        Changed?.Invoke();
    }

    public void Archive(string path)
    {
        string full = Path.GetFullPath(path);
        lock (_gate)
        {
            if (State.Cuts.TryGetValue(full, out var st)) st.Archived = true;
            if (string.Equals(State.Current, full, StringComparison.OrdinalIgnoreCase))
                State.Current = null;
            Persist();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// 彻底删除一张卡的记录（连同它的「你的时间」历史）。
    /// 数据从 state.json 移除；events.jsonl 的历史事件保留（那是账本，不删）。
    /// </summary>
    public void RemoveCut(string path)
    {
        string full = Path.GetFullPath(path);
        lock (_gate)
        {
            State.Cuts.Remove(full);
            _readFailures.Remove(full);
            if (string.Equals(State.Current, full, StringComparison.OrdinalIgnoreCase))
                State.Current = null;
            Persist();
        }
        Changed?.Invoke();
    }

    public void UpdateSettings(Action<AppState> mutate)
    {
        lock (_gate)
        {
            mutate(State);
            Persist();
        }
        Changed?.Invoke();
    }

    // ---------------- 轮询 ----------------

    private void Tick()
    {
        try
        {
            string? fg = NativeMethods.ForegroundProcessName();
            bool painting = fg is not null && PaintProcesses.Contains(fg, StringComparer.OrdinalIgnoreCase);
            ForegroundPaintApp = painting ? fg : null;

            // 「本次」只在作画软件位于前台、且人没离开、且没手动暂停时累加
            if (painting && !SessionPaused &&
                NativeMethods.IdleTime() < TimeSpan.FromMinutes(State.IdleMinutes))
                SessionTime += TimeSpan.FromSeconds(1);

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private void PollFiles()
    {
        try
        {
            SyncWorkspaceRoot();

            List<string> paths;
            lock (_gate) paths = State.Cuts.Where(kv => !kv.Value.Archived)
                                           .Select(kv => kv.Key).ToList();

            bool dirty = false;
            foreach (string p in paths)
            {
                if (HarvestOne(p)) dirty = true;
            }
            if (dirty) Changed?.Invoke();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>
    /// 自动把你当前的工作目录纳入监视。
    ///
    /// 起因：用户在新目录里打开一张卡却没能自动登记 —— 因为那个目录根本没被监视。
    /// 而 CSP 会把「打开/保存 .clip 对话框最后用的目录」记在自己的设置里，
    /// 读出来就能跟上，不用每次手动添加目录。
    /// 节流到 30 秒一次（读的是 CSP 的小型设置库，不贵但也没必要每秒读）。
    /// </summary>
    private void SyncWorkspaceRoot()
    {
        if (DateTime.UtcNow - _lastWorkspaceCheck < TimeSpan.FromSeconds(30)) return;
        _lastWorkspaceCheck = DateTime.UtcNow;

        string? folder = CspWorkspace.ReadLastDocumentFolder();
        if (folder is null) return;

        // 已经被某个监视目录覆盖（含子目录）就不用加
        lock (_gate)
        {
            bool covered = State.WatchRoots.Any(r =>
                folder.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                folder.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            if (covered) return;

            State.WatchRoots.Add(folder);
        }

        Log($"auto-added workspace root from CSP: {folder}");
        Persist();
        SetupWatchers();
        Changed?.Invoke();
    }

    /// <summary>检查单个文件是否刚被保存；是则结算增量。返回是否有变化。</summary>
    private bool HarvestOne(string path)
    {
        if (!File.Exists(path)) return false;

        FileInfo fi;
        try { fi = new FileInfo(path); } catch { return false; }

        lock (_gate)
        {
            if (!State.Cuts.TryGetValue(path, out var st)) return false;
            if (st.MtimeTicks == fi.LastWriteTimeUtc.Ticks && st.Size == fi.Length)
                return false;   // 没变化，跳过读取（这是轮询廉价的关键）

            ClipReading r = ClipWorkTime.Read(path);
            LastReadAt = DateTimeOffset.Now;
            if (!r.Ok)
            {
                bool transient = r.Note is "busy" or "file-not-found";
                if (transient)
                {
                    // 文件还在写入 / 暂时读不到：不更新 mtime，让下个轮询周期重试。
                    // 加个限次避免坏文件导致每 2 秒白读 4 MB。
                    _readFailures.TryGetValue(path, out int n);
                    if (n < MaxReadRetries)
                    {
                        _readFailures[path] = n + 1;
                        return false;      // 没更新 mtime → 下轮会再次读到"有变化"并重试
                    }
                    // 重试太多次了，接受现状，标记已看过，避免死循环
                    LastError = $"{Path.GetFileName(path)}: 持续读取失败（{r.Note}），已跳过";
                }
                else
                {
                    LastError = $"{Path.GetFileName(path)}: {r.Note}";
                }
                _readFailures.Remove(path);
                st.MtimeTicks = fi.LastWriteTimeUtc.Ticks;
                st.Size = fi.Length;
                Persist();
                return true;
            }

            _readFailures.Remove(path);

            if (st.LastSeen == 0)          // 首次（例如状态文件被删）
            {
                st.Baseline = r.WorkTimeMs;
                st.LastSeen = r.WorkTimeMs;
            }

            long delta = r.WorkTimeMs - st.LastSeen;
            if (delta < 0)
            {
                // 文件被替换 / 版本回退 —— 重置基线避免负数，但保留已累计的"我的时间"
                st.Baseline = r.WorkTimeMs;
                delta = 0;
                LastError = $"{Path.GetFileName(path)}: 作业时间回退，已重置基线";
            }
            else if (delta > 0)
            {
                st.Mine += delta;
                _store.AppendEvent(new CutEvent(DateTimeOffset.Now, st.Display, path,
                                                delta, r.WorkTimeMs, st.Mine));
            }

            st.LastSeen = r.WorkTimeMs;
            st.LastActive = DateTimeOffset.Now;
            st.MtimeTicks = fi.LastWriteTimeUtc.Ticks;
            st.Size = fi.Length;
            LastError = null;

            // 刚被保存的文件大概率就是你在画的那张 → 自动切卡
            if (State.AutoSwitchBySave &&
                !string.Equals(State.Current, path, StringComparison.OrdinalIgnoreCase))
            {
                State.Current = path;
                SessionTime = TimeSpan.Zero;
            }

            Persist();
            return true;
        }
    }

    private void TouchFileStats(string path, CutState st)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists)
            {
                st.MtimeTicks = fi.LastWriteTimeUtc.Ticks;
                st.Size = fi.Length;
            }
        }
        catch { /* 忽略 */ }
    }

    private void Persist()
    {
        try
        {
            _store.Save(State);
            _lastPersist.Restart();
        }
        catch (Exception ex)
        {
            LastError = "保存状态失败: " + ex.Message;
        }
    }

    public void Dispose()
    {
        TearDownWatchers();
        _fileTimer.Dispose();
        _tickTimer.Dispose();
    }
}
