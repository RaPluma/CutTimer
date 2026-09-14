namespace CutTimer.Core;

/// <summary>某一天的作画合计。</summary>
public sealed record DayStat(DateOnly Date, long Ms)
{
    public string Label => $"{Date.Month}/{Date.Day}";

    /// <summary>柱子上方的悬浮提示文字。</summary>
    public string Tip => Ms > 0
        ? $"{Date.Month} 月 {Date.Day} 日\n{StatsService.Fmt(Ms)}"
        : $"{Date.Month} 月 {Date.Day} 日\n没有记录";
}

/// <summary>
/// 每日作画统计。
///
/// 数据源是 events.jsonl —— 它只追加、永不重写，天然就是时间序列
/// （这是当初选择 JSONL 而不是 SQLite 的收益之一：不需要额外的表就能算每日明细）。
///
/// 注意口径：事件只在**保存**时结算，所以时长记在「保存发生的那一天」。
/// 跨零点连续作画时，整段会算到保存那一刻所在的日子。
/// </summary>
public static class StatsService
{
    /// <summary>最近 days 天（含今天）的每日合计，按日期升序，没有记录的天补 0。</summary>
    public static List<DayStat> DailyTotals(IEnumerable<CutEvent> events, int days)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        DateOnly from = today.AddDays(-(days - 1));

        var byDay = new Dictionary<DateOnly, long>();
        foreach (CutEvent e in events)
        {
            if (e.DeltaMs <= 0) continue;
            DateOnly d = DateOnly.FromDateTime(e.At.LocalDateTime);
            if (d < from || d > today) continue;
            byDay[d] = byDay.TryGetValue(d, out long v) ? v + e.DeltaMs : e.DeltaMs;
        }

        var list = new List<DayStat>(days);
        for (int i = 0; i < days; i++)
        {
            DateOnly d = from.AddDays(i);
            list.Add(new DayStat(d, byDay.TryGetValue(d, out long ms) ? ms : 0));
        }
        return list;
    }

    /// <summary>最近 days 天的合计（含今天）。</summary>
    public static long SumRecent(IEnumerable<CutEvent> events, int days)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        DateOnly from = today.AddDays(-(days - 1));

        long sum = 0;
        foreach (CutEvent e in events)
        {
            if (e.DeltaMs <= 0) continue;
            DateOnly d = DateOnly.FromDateTime(e.At.LocalDateTime);
            if (d >= from && d <= today) sum += e.DeltaMs;
        }
        return sum;
    }

    /// <summary>把毫秒格式化成「1 小时 05 分」这种好读的形式。</summary>
    public static string Fmt(long ms)
    {
        long totalSeconds = ms / 1000;
        if (totalSeconds <= 0) return "—";

        long h = totalSeconds / 3600;
        long m = totalSeconds % 3600 / 60;

        if (h > 0) return $"{h} 小时 {m:D2} 分";
        if (m > 0) return $"{m} 分";
        return $"{totalSeconds} 秒";
    }
}
