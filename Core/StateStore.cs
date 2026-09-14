using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CutTimer.Core;

/// <summary>一张卡的追踪状态。</summary>
public sealed class CutState
{
    /// <summary>首次见到该文件时的作业时间（= 上游累计）。仅用于抵消，从不显示为"你的"。</summary>
    public long Baseline { get; set; }

    /// <summary>你自己的累计增量（毫秒）。</summary>
    public long Mine { get; set; }

    /// <summary>上一次读到的文件作业时间，用来算下一次增量。</summary>
    public long LastSeen { get; set; }

    /// <summary>文件修改时间（UTC ticks）与大小，用于检测文件被替换。</summary>
    public long MtimeTicks { get; set; }
    public long Size { get; set; }

    public DateTimeOffset FirstSeen { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset LastActive { get; set; } = DateTimeOffset.Now;

    /// <summary>显示名（默认取文件名去掉扩展名）。</summary>
    public string Display { get; set; } = "";

    /// <summary>标记为隐藏/归档（不进主列表，但数据保留）。</summary>
    public bool Archived { get; set; }
}

public sealed class AppState
{
    public int Version { get; set; } = 1;

    /// <summary>绝对路径 → 状态。用 OrdinalIgnoreCase 比较（Windows 路径不区分大小写）。</summary>
    public Dictionary<string, CutState> Cuts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前正在作业的卡（绝对路径）。</summary>
    public string? Current { get; set; }

    // ---- 提醒设置 ----
    public bool ReminderEnabled { get; set; } = true;
    public int StandUpMinutes { get; set; } = 45;
    public int EyeRestMinutes { get; set; } = 30;
    public bool EyeRestEnabled { get; set; } = false;
    public int IdleMinutes { get; set; } = 5;

    /// <summary>自动把"刚被保存的文件"设为当前卡。</summary>
    public bool AutoSwitchBySave { get; set; } = true;

    /// <summary>
    /// 监视这些目录，任何 .clip 被保存就自动登记 —— 这是「不用手动添加」的实现。
    /// 只登记你真正保存过的文件，不是目录里的全部 .clip。
    /// </summary>
    public List<string> WatchRoots { get; set; } = new();

    // ---- 悬浮小窗 ----

    /// <summary>是否显示始终置顶的悬浮小窗。</summary>
    public bool MiniWindowEnabled { get; set; }

    /// <summary>悬浮小窗位置（屏幕坐标）；null 表示尚未定位过，用默认位置。</summary>
    public int? MiniX { get; set; }
    public int? MiniY { get; set; }

    /// <summary>悬浮小窗尺寸（物理像素）；null 表示用默认尺寸。</summary>
    public int? MiniWidth { get; set; }
    public int? MiniHeight { get; set; }

    /// <summary>悬浮小窗是否始终置顶（默认开）。</summary>
    public bool MiniAlwaysOnTop { get; set; } = true;

    /// <summary>
    /// 是否已经做过一次全盘 .clip 扫描。
    /// 只做一次：扫描要遍历磁盘，没必要每次启动都来一遍。
    /// </summary>
    public bool WorkspaceScanDone { get; set; }

    /// <summary>
    /// 自定义提醒音效的 wav 路径。
    /// 空 = 用内置的 Assets\chime.wav；选了系统音效时存别名（如 "alias:Notification.Reminder"）。
    /// </summary>
    public string? AlertSoundPath { get; set; }

    /// <summary>上次停留的页面标签（timer / stats / settings / about），下次启动直接回到那页。</summary>
    public string? LastPage { get; set; }
}

/// <summary>一条结算事件（写入 events.jsonl，只追加不重写）。</summary>
public sealed record CutEvent(
    [property: JsonPropertyName("t")] DateTimeOffset At,
    [property: JsonPropertyName("cut")] string Cut,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("delta")] long DeltaMs,
    [property: JsonPropertyName("total")] long TotalMs,
    [property: JsonPropertyName("mine")] long MineMs);

/// <summary>
/// 状态持久化。
///
/// 设计取舍（与用户讨论后确定）：
///   - 数据量极小（数年 &lt; 1 MB），性能不是考量点，取舍看的是「零依赖 / 可读可改 / 崩溃安全」。
///   - state.json 覆盖写，但走**原子替换**：先写 .tmp 再 File.Replace，
///     断电或崩溃时要么是完整旧文件、要么是完整新文件，绝不会是半个。
///   - events.jsonl 只追加，永不重写：没有损坏风险，且天然是时间序列，
///     「每日投入明细」直接从它算出来，不需要额外的表。
/// </summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions LineOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Root { get; }
    public string StatePath => Path.Combine(Root, "state.json");
    public string BackupPath => Path.Combine(Root, "state.json.bak");
    public string EventsPath => Path.Combine(Root, "events.jsonl");

    public StateStore(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CutTimer");
        Directory.CreateDirectory(Root);
    }

    /// <summary>读取状态；文件损坏或不存在时回退到备份 / 空状态。</summary>
    public AppState Load()
    {
        foreach (string p in new[] { StatePath, BackupPath })
        {
            if (!File.Exists(p)) continue;
            try
            {
                string json = File.ReadAllText(p, Encoding.UTF8);
                var s = JsonSerializer.Deserialize<AppState>(json, JsonOpts);
                if (s is not null)
                {
                    s.Cuts = new Dictionary<string, CutState>(s.Cuts, StringComparer.OrdinalIgnoreCase);
                    return s;
                }
            }
            catch
            {
                // 落到下一个候选（备份），最终回退到空状态
            }
        }
        return new AppState();
    }

    /// <summary>原子写入状态。</summary>
    public void Save(AppState state)
    {
        string tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOpts), new UTF8Encoding(false));

        if (File.Exists(StatePath))
        {
            // NTFS 上 File.Replace 走 ReplaceFile，是原子替换
            File.Replace(tmp, StatePath, BackupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, StatePath);
        }
    }

    /// <summary>追加一条事件。失败不影响主流程（最多丢一行）。</summary>
    public void AppendEvent(CutEvent e)
    {
        try
        {
            File.AppendAllText(EventsPath,
                JsonSerializer.Serialize(e, LineOpts) + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch
        {
            // 事件日志是辅助数据，写不进去不应影响计时
        }
    }

    /// <summary>读取全部事件（供统计/导出用）。</summary>
    public IEnumerable<CutEvent> ReadEvents()
    {
        if (!File.Exists(EventsPath)) yield break;
        foreach (string line in File.ReadLines(EventsPath, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            CutEvent? e = null;
            try { e = JsonSerializer.Deserialize<CutEvent>(line, LineOpts); } catch { }
            if (e is not null) yield return e;
        }
    }
}
