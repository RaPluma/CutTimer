using CutTimer.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;

namespace CutTimer;

/// <summary>
/// 始终置顶的悬浮小窗，参照 Win11 时钟的置顶计时窗：
/// 标题行（名称 + 置顶 + 关闭）、粗圆环 + 大字时间、底部操作按钮。
///
/// 实现要点：
///   - 置顶用 OverlappedPresenter.IsAlwaysOnTop，而不是 SetWindowPos(HWND_TOPMOST)，
///     这样和 WinUI 的窗口管理兼容，也不会被其它置顶窗口抢走层级。
///   - 圆环自己用 Path + ArcSegment 画：WinUI 的 ProgressRing 描边太细，
///     做不出时钟那种粗环效果。
///   - 拖动是「自己移窗」，不是给系统发 WM_NCLBUTTONDOWN + HTCAPTION。
///     后者会进入系统模态移动循环，按键状态一旦不一致就会失控
///     （松开鼠标后窗口还跟着跑），详见 OnRootPointerMoved 的注释。
/// </summary>
public sealed partial class MiniWindow : Window
{
    private const double DefaultRingSize = 156;

    private double _ringSize = DefaultRingSize;
    private double _ringStroke = 10;
    private double _badgeSize = 52;
    private double _badgeStroke = 5;

    /// <summary>
    /// 窗口能被拖到的最小尺寸（物理像素）。
    /// 再小内容就挤成一团了 —— 大圆环会压到标题行和信息行上（实测 309x340 就是这样）。
    /// </summary>
    private const int MinWindowW = 350;
    private const int MinWindowH = 375;

    private readonly CutTracker _tracker;
    private readonly DispatcherTimer _timer;
    private Point _pressPoint;
    private bool _pressed;
    private bool _dragging;
    private PointInt32 _winOrigin;      // 按下瞬间的窗口位置
    private int _cursorOriginX;         // 按下瞬间的光标位置
    private int _cursorOriginY;
    private bool _restoring;
    private bool _pinned;

    // 「实时」显示用的基准：上次 Mine 变化时的值与当时的本次时长。
    // 保存会让 Mine 跳一次，那时把基准重新对齐，避免把同一段时间算两遍。
    private long _mineBaseline = -1;
    private TimeSpan _sessionAtBaseline;

    public MiniWindow(CutTracker tracker)
    {
        _tracker = tracker;
        InitializeComponent();

        // csp.png 不进仓库，本机没装 CSP 时不存在 —— 那就把图标位收起来
        App.FitAppIcon(AppIcon);

        // 每次开窗清空日志，方便排查（按钮点不动时看这里）
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutTimer");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "mini.log"), "");
        }
        catch { }

        SetupWindow();

        // 注意：PointerPressed/Moved/Released 已在 XAML 里挂好，
        // 这里绝不能再挂一次 —— 重复订阅会让拖动逻辑跑两遍。
        AppWindow.Changed += OnAppWindowChanged;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Closed += (_, _) => _timer.Stop();
        Refresh();
        AnimateIn();
    }

    /// <summary>开窗时淡入 + 轻微放大，比「啪」地出现柔和。</summary>
    private void AnimateIn()
    {
        try
        {
            Root.Opacity = 0;
            var scale = new ScaleTransform { ScaleX = 0.94, ScaleY = 0.94, CenterX = 160, CenterY = 150 };
            Root.RenderTransform = scale;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = new Duration(TimeSpan.FromMilliseconds(220));

            var fade = new DoubleAnimation { From = 0, To = 1, Duration = duration, EasingFunction = ease };
            Storyboard.SetTarget(fade, Root);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var sx = new DoubleAnimation { From = 0.94, To = 1, Duration = duration, EasingFunction = ease };
            Storyboard.SetTarget(sx, scale);
            Storyboard.SetTargetProperty(sx, "ScaleX");

            var sy = new DoubleAnimation { From = 0.94, To = 1, Duration = duration, EasingFunction = ease };
            Storyboard.SetTarget(sy, scale);
            Storyboard.SetTargetProperty(sy, "ScaleY");

            var sb = new Storyboard();
            sb.Children.Add(fade);
            sb.Children.Add(sx);
            sb.Children.Add(sy);
            sb.Begin();
        }
        catch
        {
            Root.Opacity = 1;
        }
    }

    private void SetupWindow()
    {
        try { SystemBackdrop = new DesktopAcrylicBackdrop(); }
        catch { /* 亚克力不可用时用默认背景 */ }

        _pinned = _tracker.State.MiniAlwaysOnTop;

        try
        {
            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                p.IsAlwaysOnTop = _pinned;
                p.IsMaximizable = false;
                p.IsMinimizable = false;
                // 保留边框（去掉标题栏）：边框提供拖动边缘改大小的热区。
                // 用 SetBorderAndTitleBar(false,false) 的话窗口没有 WS_THICKFRAME，
                // 边缘就抓不住，没法缩放。
                p.IsResizable = true;
                p.SetBorderAndTitleBar(true, false);
            }
        }
        catch { /* 忽略 */ }

        RestoreSize();
        RestorePosition();
        App.ApplyWindowIcon(this);
    }

    /// <summary>恢复上次的尺寸；没有就用默认值。Resize 收的是物理像素。</summary>
    private void RestoreSize()
    {
        try
        {
            int w = _tracker.State.MiniWidth ?? 400;
            int h = _tracker.State.MiniHeight ?? 380;
            // 别小到把内容挤没（旧版本可能存了个过小的尺寸）
            w = Math.Max(w, MinWindowW);
            h = Math.Max(h, MinWindowH);
            AppWindow.Resize(new SizeInt32(w, h));

            // 顶过之后的尺寸写回去，否则每次启动都要再纠一遍
            if (w != _tracker.State.MiniWidth || h != _tracker.State.MiniHeight)
            {
                _tracker.UpdateSettings(s =>
                {
                    s.MiniWidth = w;
                    s.MiniHeight = h;
                });
            }
        }
        catch { /* 忽略 */ }
    }

    // ---------------- 位置 ----------------

    private void RestorePosition()
    {
        _restoring = true;
        try
        {
            int x, y;
            if (_tracker.State.MiniX is int sx && _tracker.State.MiniY is int sy)
            {
                x = sx; y = sy;
            }
            else
            {
                var wa = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
                x = wa.X + wa.Width - AppWindow.Size.Width - 24;
                y = wa.Y + (int)(wa.Height * 0.22);
            }

            // 夹回可见区域，避免显示器拔掉后小窗跑到屏幕外
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int cw = AppWindow.Size.Width, ch = AppWindow.Size.Height;
            x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - cw));
            y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - ch));

            AppWindow.Move(new PointInt32(x, y));
        }
        catch { /* 忽略 */ }
        finally { _restoring = false; }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_restoring) return;
        try
        {
            // 拖得比最小尺寸还小时顶回去。
            // OverlappedPresenter 没有 MinWidth/MinHeight，只能自己纠。
            if (args.DidSizeChange)
            {
                var cur = sender.Size;
                int w = Math.Max(cur.Width, MinWindowW);
                int h = Math.Max(cur.Height, MinWindowH);
                if (w != cur.Width || h != cur.Height)
                {
                    _restoring = true;   // 别把这次纠正当成用户改尺寸存下来
                    try { sender.Resize(new SizeInt32(w, h)); }
                    finally { _restoring = false; }
                    return;
                }
            }

            var pos = sender.Position;
            var size = sender.Size;

            if (args.DidPositionChange || args.DidSizeChange)
            {
                _tracker.UpdateSettings(s =>
                {
                    s.MiniX = pos.X;
                    s.MiniY = pos.Y;
                    s.MiniWidth = size.Width;
                    s.MiniHeight = size.Height;
                });
            }
        }
        catch { /* 忽略 */ }
    }

    // ---------------- 交互 ----------------

    private static void Log(string m)
    {
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutTimer");
            Directory.CreateDirectory(root);
            File.AppendAllText(Path.Combine(root, "mini.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {m}{Environment.NewLine}");
        }
        catch { }
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed) return;

        // 点在按钮上时不要进入拖动逻辑，否则按钮点不动
        if (e.OriginalSource is DependencyObject src && IsInteractive(src)) return;

        _pressed = true;
        _dragging = false;
        _pressPoint = e.GetCurrentPoint(Root).Position;

        // 记下起点：窗口位置 + 光标位置（都是物理像素，同一个坐标系）
        _winOrigin = AppWindow.Position;
        NativeMethods.TryGetCursorPos(out _cursorOriginX, out _cursorOriginY);

        // 抓住指针，保证 Moved / Released 一定送得到这里
        try { Root.CapturePointer(e.Pointer); } catch { /* 忽略 */ }
    }

    /// <summary>
    /// 拖动窗口。
    ///
    /// 走的是「自己移窗」而不是给系统发 WM_NCLBUTTONDOWN + HTCAPTION。
    /// 后者会让系统进入模态移动循环，一旦进入时按键状态有任何不一致
    /// （PointerReleased 丢了、按下与抬起交错），系统就会一直等"抬起"，
    /// 窗口于是跟着鼠标跑到下一次点击 —— 就是「松开鼠标后窗口还跟着鼠标」。
    ///
    /// 自己移窗则只在「本函数被调用」且「左键确实按着」时才动，
    /// 从机制上不可能失控；而且不用模态循环，UI 全程可响应。
    /// </summary>
    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;

        var pt = e.GetCurrentPoint(Root);

        // 两道防线：事件状态 + 向系统核实物理按键
        if (!pt.Properties.IsLeftButtonPressed || !NativeMethods.IsLeftButtonDown())
        {
            _pressed = false;
            _dragging = false;
            return;
        }

        if (!_dragging)
        {
            // 超过阈值才算拖动，单纯的点击不会把窗口挪走
            if (Math.Abs(pt.Position.X - _pressPoint.X) <= 3 &&
                Math.Abs(pt.Position.Y - _pressPoint.Y) <= 3) return;

            _dragging = true;
            Log("drag start");
        }

        if (!NativeMethods.TryGetCursorPos(out int cx, out int cy)) return;

        // 用「按下时光标 → 当前光标」的绝对位移，不是逐帧增量，所以不会累积漂移
        try
        {
            AppWindow.Move(new PointInt32(
                _winOrigin.X + (cx - _cursorOriginX),
                _winOrigin.Y + (cy - _cursorOriginY)));
        }
        catch { /* 忽略 */ }
    }

    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e) => EndDrag();

    private void EndDrag()
    {
        _pressed = false;
        _dragging = false;
        try { Root.ReleasePointerCaptures(); } catch { /* 忽略 */ }
    }

    /// <summary>沿视觉树往上找，判断这个元素是不是可交互控件（按钮之类）。</summary>
    private static bool IsInteractive(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>关闭小窗。切回主窗口由宿主的 Closed 回调统一处理，这里只管关。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Log("CloseClick");
        Close();
    }

    /// <summary>置顶开关，跟 Win11 时钟悬浮窗右上角那个一样。</summary>
    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        Log("PinClick");
        _pinned = !_pinned;
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter p) p.IsAlwaysOnTop = _pinned;
        }
        catch { /* 忽略 */ }
        _tracker.UpdateSettings(s => s.MiniAlwaysOnTop = _pinned);
        Refresh();
    }

    // ---------------- 刷新 ----------------

    private void Refresh()
    {
        CutState? cur = _tracker.Current;
        MiniCut.Text = cur?.Display ?? "等待中…";

        TimeSpan s = _tracker.SessionTime;
        MiniSession.Text = s.TotalHours >= 1
            ? $"{(int)s.TotalHours}:{s.Minutes:D2}:{s.Seconds:D2}"
            : $"{s.Minutes}:{s.Seconds:D2}";

        if (cur is not null)
        {
            // Mine 变了 = 刚保存过。重新对齐基准，否则同一段时间会被算两遍：
            // 保存后 Mine 已经包含了这段时间，本次计时却还在同一个区间里。
            if (cur.Mine != _mineBaseline)
            {
                _mineBaseline = cur.Mine;
                _sessionAtBaseline = s;
            }

            // 两次保存之间，用本次计时把数字撑起来，这样画面是活的
            long live = (long)Math.Max(0, (s - _sessionAtBaseline).TotalMilliseconds);
            long baseTotal = cur.LastSeen > 0 ? cur.LastSeen : cur.Baseline + cur.Mine;

            MiniTotal.Text = Fmt(baseTotal + live);
            MiniMine.Text = Fmt(cur.Mine + live);
        }
        else
        {
            _mineBaseline = -1;
            MiniTotal.Text = "—";
            MiniMine.Text = Fmt((long)s.TotalMilliseconds);
        }

        double intervalMin = Math.Max(5, _tracker.State.StandUpMinutes);
        UpdateRing(s.TotalMinutes / intervalMin * 100.0);

        MiniState.Text = "本次";

        PinBtn.Content = _pinned ? "\uE840" : "\uE77A";                     // 已置顶 / 未置顶
        PinBtn.ToolTipServiceSet(_pinned ? "取消置顶" : "始终置顶");

        // 作画状态：在作画时图标点亮、文字变强调色，圆环也一起变亮
        bool painting = _tracker.ForegroundPaintApp is not null;
        AppIcon.Opacity = painting ? 1.0 : 0.35;
        AppText.Text = painting ? "作画中" : "未在作画";
        AppText.Opacity = painting ? 1.0 : 0.55;
        try
        {
            AppText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                painting ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
        }
        catch { /* 取不到主题资源就保持默认色 */ }

        RingArc.Opacity = painting ? 1.0 : 0.35;

        UpdateMiniCountdown();
    }

    /// <summary>
    /// 窗口被拖动边缘改变大小时，圆环和中心字号跟着缩放，
    /// 否则窗口拉大后一个小圆圈孤零零浮在中间，很空。
    /// </summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width < 60 || e.NewSize.Height < 60) return;

        // 标题行 / 信息行 / 底部状态条占掉固定高度，剩下的给圆环
        double avail = Math.Min(e.NewSize.Width - 48, e.NewSize.Height - 178);
        double size = Math.Clamp(avail, 92, 320);

        // 左下角那枚倒计时小圈跟着大圈等比缩放：
        // 固定尺寸的话，窗口缩小时小圈会显得比大圈还大（实测踩过）
        double badge = Math.Clamp(size * 0.34, 40, 62);

        if (Math.Abs(size - _ringSize) < 0.5 && Math.Abs(badge - _badgeSize) < 0.5) return;

        _ringSize = size;
        _ringStroke = Math.Clamp(size * 0.065, 6, 15);
        _badgeSize = badge;
        _badgeStroke = Math.Clamp(badge * 0.10, 3.5, 6);

        RingHost.Width = size;
        RingHost.Height = size;
        RingTrack.Width = size;
        RingTrack.Height = size;
        RingTrack.StrokeThickness = _ringStroke;
        RingArc.StrokeThickness = _ringStroke;

        MiniSession.FontSize = Math.Clamp(size * 0.195, 17, 46);
        MiniState.FontSize = Math.Clamp(size * 0.070, 9, 15);

        MiniCdHost.Width = badge;
        MiniCdHost.Height = badge;
        MiniCdFill.Width = badge;
        MiniCdFill.Height = badge;
        MiniCdTrack.Width = badge;
        MiniCdTrack.Height = badge;
        MiniCdTrack.StrokeThickness = _badgeStroke;
        MiniCdArc.StrokeThickness = _badgeStroke;
        MiniCdText.FontSize = Math.Clamp(badge * 0.26, 10, 16);

        Refresh();
    }

    /// <summary>自绘进度弧：从 12 点方向顺时针。</summary>
    private void UpdateRing(double pct)
    {
        RingArc.Data = BuildArc(_ringSize, _ringStroke, pct);
    }

    /// <summary>小圈（提醒倒计时）的弧。</summary>
    private void UpdateMiniCdArc(double progress)
    {
        MiniCdArc.Data = BuildArc(_badgeSize, _badgeStroke, progress * 100.0);
    }

    /// <summary>
    /// 造一段圆弧几何。大圈和左下角的小圈共用 ——
    /// 两者只差尺寸和线宽，没必要写两遍。
    /// </summary>
    private static PathGeometry? BuildArc(double size, double stroke, double pct)
    {
        double clamped = Math.Clamp(pct, 0, 100);
        if (clamped < 0.15) return null;

        // 满圈时留一点点缺口，否则起点终点重合会画不出圆头
        double sweep = clamped / 100.0 * 359.9;
        double radius = (size - stroke) / 2;
        double cx = size / 2;
        double cy = size / 2;

        Point At(double deg)
        {
            double rad = deg * Math.PI / 180.0;
            return new Point(cx + radius * Math.Cos(rad), cy + radius * Math.Sin(rad));
        }

        const double start = -90.0;   // 12 点方向
        var fig = new PathFigure
        {
            StartPoint = At(start),
            IsClosed = false,
            IsFilled = false,
        };
        fig.Segments.Add(new ArcSegment
        {
            Point = At(start + sweep),
            Size = new Size(radius, radius),
            IsLargeArc = sweep > 180,
            SweepDirection = SweepDirection.Clockwise,
        });

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    /// <summary>
    /// 刷新左下角那枚提醒倒计时小圈。
    /// 只显示分钟（小圈里塞不下 分:秒），完整的剩余时间放在悬浮提示里。
    /// </summary>
    private void UpdateMiniCountdown()
    {
        if (MiniCdText is null) return;

        App app = (App)Application.Current;
        ReminderService rem = app.Reminders;

        if (!_tracker.State.ReminderEnabled)
        {
            MiniCdText.Text = "—";
            MiniCdArc.Data = null;
            MiniCdTrack.Opacity = 0.35;
            MiniCdText.Opacity = 0.35;
            MiniCdHost.ToolTipServiceSet("起身提醒已关闭");
            return;
        }

        if (rem.HasFired)
        {
            // 到点了：圆环走满、写个感叹号
            MiniCdText.Text = "!";
            UpdateMiniCdArc(1.0);
            MiniCdTrack.Opacity = 1.0;
            MiniCdText.Opacity = 1.0;
            MiniCdHost.ToolTipServiceSet("该起来活动了 —— 点「重置」开始新一轮");
            return;
        }

        TimeSpan left = rem.UntilStandUp;
        MiniCdText.Text = $"{(int)Math.Ceiling(left.TotalMinutes)}";
        UpdateMiniCdArc(rem.StandUpProgress);

        bool paused = rem.IsPaused;
        MiniCdTrack.Opacity = paused ? 0.35 : 1.0;
        MiniCdText.Opacity = paused ? 0.5 : 1.0;
        MiniCdHost.ToolTipServiceSet(paused
            ? $"提醒已暂停 · 还剩 {left.Minutes}:{left.Seconds:D2}"
            : $"距下次起身提醒 {left.Minutes}:{left.Seconds:D2}");
    }

    private static string Fmt(long ms)
    {
        long t = ms / 1000;
        return $"{t / 3600}:{t % 3600 / 60:D2}:{t % 60:D2}";
    }
}

internal static class ToolTipExt
{
    /// <summary>ToolTipService.ToolTip 没有强类型 setter，用附加属性设置。</summary>
    public static void ToolTipServiceSet(this DependencyObject o, string text)
        => Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(o, text);
}
