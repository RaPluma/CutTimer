using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CutTimer;

/// <summary>监视目录列表的一行。</summary>
public sealed class RootRow
{
    public string Path { get; }
    public RootRow(string path) => Path = path;
}

/// <summary>
/// 卡列表的一行。
///
/// 实现 INotifyPropertyChanged 是为了**避免闪烁**：
/// 如果每次刷新都重建 ItemsSource，ListView 会重建全部行容器，
/// 表现为列表闪动、滚动位置丢失、选中态乱跳。
/// 正确做法是 ItemsSource 只设一次，之后原地更新属性。
///
/// 必须是顶层类型，XAML 的 x:DataType 才能引用。
/// </summary>
public sealed class CutRow : INotifyPropertyChanged
{
    public string Path { get; }

    private string _title = "";
    private string _detail = "";
    private string _mineShort = "—";

    public CutRow(string path, Core.CutState state)
    {
        Path = path;
        Update(state);
    }

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string MineShort { get => _mineShort; private set => Set(ref _mineShort, value); }

    /// <summary>用最新的状态刷新显示值。值没变则不发通知，避免无谓的重绘。</summary>
    public void Update(Core.CutState s)
    {
        long total = s.LastSeen > 0 ? s.LastSeen : s.Baseline + s.Mine;
        Title = s.Display;
        Detail = $"本卡 {Fmt(total)}　·　接手时已有 {Fmt(s.Baseline)}";
        // 即使是 0 也显示成 0:00:00 —— 显示 "—" 会让人以为这个字段是坏的
        MineShort = Fmt(s.Mine);
    }

    /// <summary>排序用的活跃时间戳，别直接绑到 UI。</summary>
    public DateTimeOffset LastActive { get; private set; }

    public void Touch(DateTimeOffset lastActive) => LastActive = lastActive;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static string Fmt(long ms)
    {
        long s = ms / 1000;
        return $"{s / 3600}:{s % 3600 / 60:D2}:{s % 60:D2}";
    }
}
