namespace CutTimer.Core;

/// <summary>扫描进度：已经检查了多少目录、找到多少个含 .clip 的目录。</summary>
public sealed record ScanProgress(int VisitedDirs, int HitFolders, string Phase);

/// <summary>
/// 一次性后台扫描：找出磁盘上最近改过的 .clip 所在目录，把它们纳入监视。
///
/// 为什么需要：光靠 CSP 的「上次目录」只能覆盖你**最近**待过的那一个文件夹，
/// 已有的工作目录（比如整部作品的文件夹树）发现不了。
/// 扫一遍就能一次性把它们都盯上，之后新保存的卡自动登记。
///
/// **只由用户手动触发**（设置页的「扫描磁盘…」按钮）。
/// 早先版本挂在首次启动上自动跑，但那样程序会在毫无预告的情况下吃几十秒磁盘 IO，
/// 对一个「只是计时」的工具来说动静太大。
///
/// 设计上刻意保守：
///   - 广度优先 + 深度上限，避开系统目录
///   - 有时间预算，超时就带着已找到的部分返回
/// </summary>
public static class WorkspaceScanner
{
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "$Recycle.Bin", "$WinREAgent", "System Volume Information", "Recovery",
        "Program Files", "Program Files (x86)", "ProgramData", "AppData",
        "node_modules", ".git", ".svn", ".vs", "packages", "obj", "bin",
    };

    /// <summary>
    /// 扫固定磁盘，返回「含近期 .clip」的目录集合。
    /// <paramref name="budget"/> 用完就返回已有结果。
    /// </summary>
    /// <summary>
    /// 扫固定磁盘，返回「含近期 .clip」的目录集合。
    /// <paramref name="budget"/> 用完就返回已有结果。
    /// <paramref name="progress"/> 会被周期性调用（大约每 40 个目录一次），
    /// 用来在界面上显示「已检查多少、找到多少」。
    /// </summary>
    public static List<string> FindRecentClipFolders(
        TimeSpan budget, int maxDepth = 5, int recentDays = 365, Action<string>? log = null,
        IProgress<ScanProgress>? progress = null)
    {
        var hits = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        DateTime deadline = DateTime.UtcNow + budget;
        DateTime cutoff = DateTime.UtcNow.AddDays(-recentDays);
        int visited = 0;
        int reported = 0;
        bool timedOut = false;

        // 不按时间报（太频繁），按访问目录数报，界面上数字跳得太快反而看不清
        void Report(string phase)
        {
            progress?.Report(new ScanProgress(visited, hits.Count, phase));
        }

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            if (DateTime.UtcNow > deadline) { timedOut = true; break; }

            string driveName = drive.Name.TrimEnd('\\');
            Report($"正在扫描 {driveName} …");

            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((drive.RootDirectory.FullName, 0));

            while (queue.Count > 0)
            {
                if (DateTime.UtcNow > deadline) { timedOut = true; break; }

                (string dir, int depth) = queue.Dequeue();
                visited++;

                if (visited - reported >= 40)
                {
                    reported = visited;
                    Report($"正在扫描 {driveName} …");
                }

                try
                {
                    // 这个目录里的 .clip 算不算"近期"
                    foreach (string f in Directory.EnumerateFiles(dir, "*.clip"))
                    {
                        try
                        {
                            DateTime t = File.GetLastWriteTimeUtc(f);
                            if (t < cutoff) continue;
                            if (!hits.TryGetValue(dir, out DateTime old) || t > old)
                                hits[dir] = t;
                        }
                        catch { /* 单个文件读不到就跳过 */ }
                    }

                    if (depth >= maxDepth) continue;

                    foreach (string sub in Directory.EnumerateDirectories(dir))
                    {
                        string name = Path.GetFileName(sub);
                        if (Skip.Contains(name)) continue;
                        if (name.StartsWith('.')) continue;
                        queue.Enqueue((sub, depth + 1));
                    }
                }
                catch { /* 没权限的目录直接跳过 */ }
            }
        }

        log?.Invoke($"scan: visited {visited} dirs, {hits.Count} folders with recent .clip" +
                    (timedOut ? " (hit time budget)" : ""));
        progress?.Report(new ScanProgress(visited, hits.Count,
            timedOut ? "已达到 40 秒预算，扫描提前结束" : "扫描完成"));
        return hits.Keys.ToList();
    }

    /// <summary>
    /// 把一堆叶子目录折叠成较少的祖先目录。
    ///
    /// 扫描会找出每个「装了 .clip 的文件夹」，一部作品动辄几百个 —— 直接全加会变成
    /// 317 个 watcher，设置页也没法看。同一部作品的子目录用它们的上层一次看住即可
    /// （watcher 本来就是递归的）。
    ///
    /// 但**不能折叠得太狠**：盘符根、盘符下一层、用户目录本身这些都太宽，会盯上整个盘
    /// 或者整个用户目录，所以到这些边界就停。
    /// </summary>
    public static List<string> Collapse(List<string> dirs)
    {
        var broad = BroadPaths();
        var cover = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // 统计每个候选祖先能覆盖多少个命中目录（最多往上找 3 层）
        foreach (string d in dirs)
        {
            string? p = Path.GetDirectoryName(d);
            for (int i = 0; i < 3 && !string.IsNullOrEmpty(p); i++)
            {
                if (IsTooBroad(p!, broad)) break;
                cover[p!] = cover.TryGetValue(p!, out int n) ? n + 1 : 1;
                p = Path.GetDirectoryName(p!);
            }
        }

        var chosen = new List<string>();

        // 从浅到深挑：能覆盖 2 个以上、且没被已选中的祖先覆盖
        foreach (var kv in cover.OrderBy(k => k.Key.Length))
        {
            if (kv.Value < 2) continue;
            if (chosen.Any(c => Covered(kv.Key, c))) continue;
            chosen.Add(kv.Key);
        }

        // 剩下的命中目录自己顶上
        foreach (string d in dirs)
        {
            if (chosen.Any(c => Covered(d, c))) continue;
            chosen.Add(d);
        }

        return chosen;
    }

    private static bool Covered(string path, string ancestor)
        => path.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith(ancestor.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsTooBroad(string path, HashSet<string> broad)
        => broad.Contains(path.TrimEnd('\\'));

    /// <summary>收集「太宽，不能当监视根」的路径。</summary>
    private static HashSet<string> BroadPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            if (!d.IsReady) continue;
            string root = d.RootDirectory.FullName.TrimEnd('\\');
            set.Add(root);
            // 盘符下第一层目录也排除
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(d.RootDirectory.FullName))
                    set.Add(sub.TrimEnd('\\'));
            }
            catch { /* 忽略 */ }
        }

        // 用户目录及其所有祖先（往上一路排到盘符根）
        foreach (Environment.SpecialFolder sf in new[]
                 {
                     Environment.SpecialFolder.UserProfile,
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.LocalApplicationData,
                 })
        {
            string p = Environment.GetFolderPath(sf).TrimEnd('\\');
            while (!string.IsNullOrEmpty(p) && p.Length > 3)
            {
                set.Add(p);
                p = Path.GetDirectoryName(p)?.TrimEnd('\\') ?? "";
            }
        }

        return set;
    }
}
