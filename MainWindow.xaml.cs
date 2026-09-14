using CutTimer.Core;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Storage.Pickers;

namespace CutTimer;

public sealed partial class MainWindow : Window
{
    private readonly CutTracker _tracker;
    private readonly DispatcherTimer _uiTimer;
    private readonly System.Collections.ObjectModel.ObservableCollection<CutRow> _rows = new();
    private readonly List<string> _rootPaths = new();
    private bool _suppressSelection;
    private bool _suppressSettings;

    public MainWindow(CutTracker tracker)
    {
        _tracker = tracker;
        InitializeComponent();
        Title = "CutTimer";

        // csp.png 不进仓库，本机没装 CSP 时不存在 —— 那就把图标位收起来
        App.FitAppIcon(AppIcon);

        ApplyMicaAndTitleBar();

        // ItemsSource 只设一次；之后靠 ObservableCollection + INotifyPropertyChanged
        // 原地更新，否则每 500ms 重建一次会让列表闪烁
        CutList.ItemsSource = _rows;

        _tracker.Changed += OnTrackerChanged;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _uiTimer.Tick += (_, _) => Refresh();
        _uiTimer.Start();

        Refresh();

        // 回到上次停留的页面（IsSelected 写在 XAML 里不生效，只能这样恢复）
        SelectPage(_tracker.State.LastPage ?? "timer");
    }

    /// <summary>Win11 原生观感：Mica 背景 + 内容延伸到标题栏。</summary>
    private void ApplyMicaAndTitleBar()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBar);
        }
        catch
        {
            // Mica 在某些环境（远程桌面/旧显卡）不可用，忽略即可，窗口仍正常
        }

        try
        {
            // 小工具不需要铺满屏幕，给个紧凑的默认尺寸
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 780));
        }
        catch { /* 老系统可能不支持，忽略 */ }

        App.ApplyWindowIcon(this);
    }

    // ---------------- 导航 ----------------

    /// <summary>左侧图标导航切换页面。</summary>
    private void OnNavChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "timer";

        PageTimer.Visibility = tag == "timer" ? Visibility.Visible : Visibility.Collapsed;
        PageStats.Visibility = tag == "stats" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;

        UIElement shown = tag switch
        {
            "stats" => PageStats,
            "settings" => PageSettings,
            "about" => PageAbout,
            _ => PageTimer,
        };
        AnimatePageIn(shown);

        if (tag == "stats") RenderChart(force: true);

        // 记住停留的页面，下次启动直接回到这里
        if (_tracker.State.LastPage != tag)
            _tracker.UpdateSettings(s => s.LastPage = tag);
    }

    /// <summary>
    /// 切到指定页面。
    ///
    /// 注意：NavigationViewItem 的 IsSelected 写在 XAML 里**不起作用**
    /// （NavigationView 认的是 SelectedItem），所以恢复页面必须走这里。
    /// </summary>
    private void SelectPage(string tag)
    {
        foreach (object item in Nav.MenuItems)
        {
            if (item is NavigationViewItem it && (it.Tag as string) == tag)
            {
                Nav.SelectedItem = it;
                return;
            }
        }
    }

    /// <summary>
    /// 页面进场动效：淡入 + 从下方轻微上滑。
    ///
    /// 用代码里的 Storyboard 而不是 XAML 的 EntranceThemeTransition ——
    /// 后者只在「子元素被加入可视树」时触发，而这里切页只是改 Visibility，
    /// 动画不会重新播放。
    /// </summary>
    private void AnimatePageIn(UIElement page)
    {
        try
        {
            var slide = new TranslateTransform { Y = 18 };
            page.RenderTransform = slide;
            page.Opacity = 0;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = new Duration(TimeSpan.FromMilliseconds(260));

            var fade = new DoubleAnimation
            {
                From = 0, To = 1, Duration = duration, EasingFunction = ease,
            };
            Storyboard.SetTarget(fade, page);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var move = new DoubleAnimation
            {
                From = 18, To = 0, Duration = duration, EasingFunction = ease,
            };
            Storyboard.SetTarget(move, slide);
            Storyboard.SetTargetProperty(move, "Y");

            var sb = new Storyboard();
            sb.Children.Add(fade);
            sb.Children.Add(move);
            sb.Begin();
        }
        catch
        {
            // 动画失败不该让页面切不过去
            page.Opacity = 1;
        }
    }

    // ---------------- 提醒倒计时 ----------------

    /// <summary>暂停 / 继续倒计时。</summary>
    private void OnPauseReminderClick(object sender, RoutedEventArgs e)
    {
        App app = (App)Application.Current;
        app.Reminders.SetPaused(!app.Reminders.IsPaused);
        UpdateCountdown();
    }

    /// <summary>重置倒计时：从现在重新算（「我刚休息过了」），并停掉还在响的提醒音。</summary>
    private void OnResetReminderClick(object sender, RoutedEventArgs e)
    {
        App app = (App)Application.Current;
        app.AcknowledgeAlert();          // 停掉循环音效
        app.Reminders.ResetBreak();      // 开始新的一轮
        UpdateCountdown();
    }

    /// <summary>刷新右卡的倒计时数字与圆环。</summary>
    private void UpdateCountdown()
    {
        if (CdTime is null) return;

        App app = (App)Application.Current;
        ReminderService rem = app.Reminders;

        // 暂停按钮的样式跟着状态走
        bool paused = rem.IsPaused;
        PauseIcon.Glyph = paused ? "\uE768" : "\uE769";   // 继续 / 暂停
        PauseText.Text = paused ? "继续" : "暂停";
        PauseReminderButton.IsEnabled = !rem.HasFired;

        // 「重置」在已提醒状态下高亮成强调色 —— 这是当前唯一该做的动作
        try
        {
            ResetReminderButton.Style = (Style)Application.Current.Resources[
                rem.HasFired ? "AccentButtonStyle" : "SubtleButtonStyle"];
        }
        catch { /* 取不到样式就保持原样 */ }

        if (!_tracker.State.ReminderEnabled)
        {
            CdTime.Text = "已关闭";
            CdCaption.Text = "起身提醒";
            CdArc.Data = null;
            CdTrack.Opacity = 0.5;
            CdArc.Opacity = 1.0;
            return;
        }

        // 已经提醒过、等你手动重开：倒计时定在满圈
        if (rem.HasFired)
        {
            CdTime.Text = "时间到";
            CdCaption.Text = "该起来了";
            CdTrack.Opacity = 1.0;
            CdArc.Opacity = 1.0;
            DrawCountdownArc(1.0);
            return;
        }

        if (paused)
        {
            // 暂停时数字定住，圆环压暗，一眼能看出「现在不走」
            TimeSpan leftPaused = rem.UntilStandUp;
            CdTime.Text = leftPaused.TotalHours >= 1
                ? $"{(int)leftPaused.TotalHours}:{leftPaused.Minutes:D2}:{leftPaused.Seconds:D2}"
                : $"{leftPaused.Minutes}:{leftPaused.Seconds:D2}";
            CdCaption.Text = "已暂停";
            CdTrack.Opacity = 0.5;
            CdArc.Opacity = 0.35;
            DrawCountdownArc(rem.StandUpProgress);
            return;
        }

        TimeSpan left = rem.UntilStandUp;
        CdTime.Text = left.TotalHours >= 1
            ? $"{(int)left.TotalHours}:{left.Minutes:D2}:{left.Seconds:D2}"
            : $"{left.Minutes}:{left.Seconds:D2}";

        // 剩 15% 时提示该准备了，用文字而不是颜色，色弱也看得懂
        CdCaption.Text = rem.StandUpProgress >= 0.85 ? "快到了" : "距下次";
        CdTrack.Opacity = 1.0;
        CdArc.Opacity = 1.0;
        DrawCountdownArc(rem.StandUpProgress);
    }

    /// <summary>
    /// 画倒计时圆环。和悬浮小窗一样自己画 ——
    /// WinUI 的 ProgressRing 描边太细，撑不起卡片里这个尺寸的视觉重量。
    /// </summary>
    private void DrawCountdownArc(double progress)
    {
        const double size = 104;
        const double stroke = 7;

        double clamped = Math.Clamp(progress, 0, 1);
        if (clamped < 0.004) { CdArc.Data = null; return; }

        double sweep = clamped * 359.9;   // 满圈留一点缺口，否则首尾重合画不出圆头
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
        CdArc.Data = geo;
    }

    // ---------------- 每日统计 ----------------

    private int _statsDays = 14;
    private DateTime _chartRenderedAt = DateTime.MinValue;

    private void OnStatsRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 里 IsSelected="True" 会在解析时就触发一次，那时 BarHost 还没建好
        if (BarHost is null) return;

        if (StatsRangeBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            int.TryParse(tag, out int days))
        {
            _statsDays = days;
            RenderChart(force: true);
        }
    }

    /// <summary>统计页可见时定期重画（节流 5 秒，避免每 500ms 重建一次柱子）。</summary>
    private void MaybeRefreshChart()
    {
        if (PageStats.Visibility != Visibility.Visible) return;
        if (DateTime.UtcNow - _chartRenderedAt < TimeSpan.FromSeconds(5)) return;
        RenderChart(force: true);
    }

    /// <summary>
    /// 画柱状图。
    ///
    /// 不用 ItemsControl + 数据模板：柱子宽度要随窗口宽度自适应，
    /// 用「每天一列、宽度 Star 平分」的 Grid 最省事也最稳，所以直接在代码里建。
    /// </summary>
    private void RenderChart(bool force = false)
    {
        if (BarHost is null || LabelHost is null) return;
        if (!force && DateTime.UtcNow - _chartRenderedAt < TimeSpan.FromSeconds(5)) return;
        _chartRenderedAt = DateTime.UtcNow;

        BarHost.Children.Clear();
        BarHost.ColumnDefinitions.Clear();
        LabelHost.Children.Clear();
        LabelHost.ColumnDefinitions.Clear();

        List<CutEvent> events = ((App)Application.Current).Store.ReadEvents().ToList();
        List<DayStat> days = StatsService.DailyTotals(events, _statsDays);
        if (days.Count == 0) return;

        long max = days.Max(d => d.Ms);
        if (max <= 0) max = 1;

        for (int i = 0; i < days.Count; i++)
        {
            BarHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            LabelHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        // 横轴标签只标首尾和每若干根，免得挤成一团
        int step = Math.Max(1, (int)Math.Ceiling(days.Count / 6.0));

        for (int i = 0; i < days.Count; i++)
        {
            DayStat d = days[i];
            bool isToday = i == days.Count - 1;
            bool hasData = d.Ms > 0;

            // 没记录的日子留一根 2px 的小墩，让「那天没画」和「没有那天」看起来不一样
            double targetHeight = hasData ? Math.Max(4, (double)d.Ms / max * 146) : 2;

            var bar = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Height = targetHeight,
                RadiusX = 3,
                RadiusY = 3,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(2, 0, 2, 0),
                Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                    hasData ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush"],
                // 「今天」用满色，往日压到 55% —— 用 Secondary 色在同主题下几乎看不出差别
                Opacity = hasData && !isToday ? 0.55 : 1.0,
            };
            ToolTipService.SetToolTip(bar, d.Tip);
            Grid.SetColumn(bar, i);
            BarHost.Children.Add(bar);

            // 柱子长出来的动效：每根按索引错开一点，看起来像依次生长
            AnimateBar(bar, targetHeight, i);

            var label = new TextBlock
            {
                Text = (isToday || i == 0 || i % step == 0) ? d.Label : "",
                FontSize = 10,
                Opacity = 0.5,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Grid.SetColumn(label, i);
            LabelHost.Children.Add(label);
        }

        StatToday.Text = StatsService.Fmt(days[^1].Ms);
        Stat7.Text = StatsService.Fmt(StatsService.SumRecent(events, 7));
        Stat30.Text = StatsService.Fmt(StatsService.SumRecent(events, 30));
    }

    /// <summary>单根柱子从 0 长到目标高度，按索引错开，整体像依次生长。</summary>
    private static void AnimateBar(FrameworkElement bar, double targetHeight, int index)
    {
        try
        {
            var grow = new DoubleAnimation
            {
                From = 0,
                To = targetHeight,
                Duration = new Duration(TimeSpan.FromMilliseconds(420)),
                BeginTime = TimeSpan.FromMilliseconds(Math.Min(index * 35, 600)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true,   // Height 不是合成属性，必须开这个
            };
            Storyboard.SetTarget(grow, bar);
            Storyboard.SetTargetProperty(grow, "Height");

            var sb = new Storyboard();
            sb.Children.Add(grow);
            sb.Begin();
        }
        catch { /* 动画失败留静态高度即可 */ }
    }

    private void OnFolderClick(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", ((App)Application.Current).Store.Root); }
        catch { /* 忽略 */ }
    }

    /// <summary>
    /// 手动触发全盘扫描。
    /// 放在后台线程跑（默认 40 秒预算），过程中显示进度条和「已检查 / 已找到」的实时数字 ——
    /// 启动时不做这件事，免得程序毫无预告地卡几十秒。
    /// </summary>
    private async void OnScanRootsClick(object sender, RoutedEventArgs e)
    {
        ScanRootsButton.IsEnabled = false;
        AddRootButton.IsEnabled = false;
        ScanPanel.Visibility = Visibility.Visible;
        ScanBar.IsIndeterminate = true;
        ScanStatusText.Text = "正在准备…";

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                ScanStatusText.Text = p.VisitedDirs > 0
                    ? $"{p.Phase}　已检查 {p.VisitedDirs:N0} 个目录，找到 {p.HitFolders} 个含 .clip 的目录"
                    : p.Phase;
            });

            int added = await Task.Run(() => _tracker.ScanWorkspaceNow(progress));
            ScanBar.IsIndeterminate = false;
            ScanBar.Value = 100;
            ScanStatusText.Text = added > 0
                ? $"扫描完成，新增 {added} 个监视目录。"
                : "扫描完成，没有发现新的目录。";
            Refresh();
        }
        catch (Exception ex)
        {
            ScanBar.IsIndeterminate = false;
            ScanStatusText.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            ScanRootsButton.IsEnabled = true;
            AddRootButton.IsEnabled = true;
        }
    }

    // ---------------- 提醒音效 ----------------

    /// <summary>试听当前音效（响一次，不循环）。</summary>
    private void OnPreviewSoundClick(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).PreviewAlertSound();
    }

    /// <summary>选择一个 .wav 作为提醒音效。</summary>
    private async void OnPickSoundClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
            picker.FileTypeFilter.Add(".wav");

            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null) return;

            ((App)Application.Current).Tracker.UpdateSettings(s => s.AlertSoundPath = file.Path);
            UpdateSoundLabel();
            ((App)Application.Current).PreviewAlertSound();
        }
        catch (Exception ex)
        {
            StatusText.Text = "选择音效失败：" + ex.Message;
        }
    }

    /// <summary>恢复内置默认铃声。</summary>
    private void OnResetSoundClick(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).Tracker.UpdateSettings(s => s.AlertSoundPath = null);
        UpdateSoundLabel();
        ((App)Application.Current).PreviewAlertSound();
    }

    private void UpdateSoundLabel()
    {
        if (SoundNameText is null) return;
        SoundNameText.Text = ((App)Application.Current).AlertSoundLabel;
    }

    /// <summary>标题栏的悬浮小窗开关。用 Click 而非 Checked —— 程序化同步状态不会触发 Click。</summary>
    private void OnMiniToggleClick(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).SetMiniVisible(MiniToggle.IsChecked == true);
    }

    /// <summary>
    /// 登记命令行带来的 .clip。必须在 Activate 之后调用：
    /// 窗口构造期间 DispatcherQueue 还没开始处理。
    /// </summary>
    public void LoadStartupFiles()
    {
        try
        {
            foreach (string a in Environment.GetCommandLineArgs().Skip(1))
            {
                if (a.StartsWith("--", StringComparison.Ordinal)) continue;
                if (a.EndsWith(".clip", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
                    _tracker.Register(a, makeCurrent: true);
            }
        }
        catch { /* 忽略 */ }
        Refresh();
    }

    private void OnTrackerChanged() => DispatcherQueue.TryEnqueue(Refresh);

    // ---------------- 监视目录 ----------------

    private async void OnAddRootClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            _tracker.UpdateSettings(s =>
            {
                if (!s.WatchRoots.Contains(folder.Path, StringComparer.OrdinalIgnoreCase))
                    s.WatchRoots.Add(folder.Path);
            });
            _tracker.RestartWatchers();
        }
        catch (Exception ex)
        {
            StatusText.Text = "添加目录失败: " + ex.Message;
        }
    }

    private void OnRemoveRootClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
        {
            _tracker.UpdateSettings(s => s.WatchRoots.RemoveAll(
                r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase)));
            _tracker.RestartWatchers();
        }
    }

    // ---------------- 卡 ----------------

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add(".clip");

            var file = await picker.PickSingleFileAsync();
            if (file is not null) _tracker.Register(file.Path, makeCurrent: true);
        }
        catch (Exception ex)
        {
            StatusText.Text = "选择文件失败: " + ex.Message;
        }
    }

    private void OnCutSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (CutList.SelectedItem is CutRow row) _tracker.SetCurrent(row.Path);
    }

    /// <summary>
    /// 给每个列表项挂右键菜单。
    ///
    /// 不用 ItemContainerStyle 里的 ContextFlyout —— 那样所有行共用同一个
    /// MenuFlyout 实例，点击时 DataContext 解析不到具体那一行，菜单点了没反应。
    /// 这里逐容器构造，闭包直接捕获 row，最可靠。
    /// </summary>
    private void OnCutContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.ItemContainer is not ListViewItem container) return;
        if (args.Item is not CutRow row) return;

        var setCurrent = new MenuFlyoutItem { Text = "设为当前" };
        setCurrent.Click += (_, _) => _tracker.SetCurrent(row.Path);

        var reset = new MenuFlyoutItem { Text = "重置基线（从现在起算我的）" };
        reset.Click += (_, _) => _tracker.ResetBaseline(row.Path);

        var del = new MenuFlyoutItem { Text = "删除记录" };
        del.Click += (_, _) => _tracker.RemoveCut(row.Path);

        var flyout = new MenuFlyout();
        flyout.Items.Add(setCurrent);
        flyout.Items.Add(reset);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(del);

        container.ContextFlyout = flyout;
    }

    // ---------------- 设置 ----------------

    /// <summary>提醒间隔的合法范围（分钟）。ReminderService 里也有个 Max(5,..) 的下限兜底。</summary>
    private const int MinInterval = 5;
    private const int MaxInterval = 600;

    private void OnReminderToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettings) return;
        _tracker.UpdateSettings(s => s.ReminderEnabled = ReminderToggle.IsOn);
    }

    /// <summary>
    /// 首次同步完成前，忽略 NumberBox 的值变化。
    ///
    /// 必须要有这个守卫：NumberBox 初次加载时 Value 是 NaN，会被 Minimum 强制成 5，
    /// 并触发一次 ValueChanged —— 那一发会把用户存的间隔直接覆盖成 5（实测踩过，
    /// 30 分钟被悄悄改成 5 分钟）。等 SetInterval 首次写入真实值后才允许回写。
    /// </summary>
    private bool _intervalReady;

    /// <summary>NumberBox 的值变了（点微调按钮、或失焦提交）。</summary>
    private void OnIntervalChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressSettings || !_intervalReady) return;
        if (double.IsNaN(args.NewValue)) return;

        int mins = Math.Clamp((int)Math.Round(args.NewValue), MinInterval, MaxInterval);
        if (mins != _tracker.State.StandUpMinutes)
            _tracker.UpdateSettings(s => s.StandUpMinutes = mins);
    }

    /// <summary>
    /// 回车提交。
    /// NumberBox 在部分版本里敲完数字按回车不会提交（用户报过这个），
    /// 所以这里自己从文本读一次，并且把非数字的输入还原回去。
    /// </summary>
    private void OnIntervalKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;

        string raw = (IntervalBox.Text ?? "").Trim();
        if (int.TryParse(raw, out int mins))
        {
            mins = Math.Clamp(mins, MinInterval, MaxInterval);
            SetInterval(mins);
            _tracker.UpdateSettings(s => s.StandUpMinutes = mins);
        }
        else
        {
            // 输入的不是数字 → 还原成当前值，别留个空框
            SetInterval(_tracker.State.StandUpMinutes);
        }
    }

    /// <summary>快捷预设按钮。</summary>
    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag) return;
        if (!int.TryParse(tag, out int mins)) return;

        SetInterval(mins);
        _tracker.UpdateSettings(s => s.StandUpMinutes = mins);
    }

    // ---------------- 刷新 ----------------

    private void Refresh()
    {
        // 只在值真的不同时才写控件，否则每 500ms 一次的刷新会让
        // ToggleSwitch / NumberBox 出现抖动
        bool reminder = _tracker.State.ReminderEnabled;
        if (ReminderToggle.IsOn != reminder)
        {
            _suppressSettings = true;
            try { ReminderToggle.IsOn = reminder; } finally { _suppressSettings = false; }
        }

        int interval = _tracker.State.StandUpMinutes;
        if (!_suppressSettings)
        {
            _suppressSettings = true;
            try { SetInterval(interval); } finally { _suppressSettings = false; }
        }

        // 悬浮小窗开关跟着实际状态走（可能从托盘改的）
        bool miniOn = ((App)Application.Current).MiniVisible;
        if (MiniToggle.IsChecked != miniOn) MiniToggle.IsChecked = miniOn;

        CutState? cur = _tracker.Current;
        if (cur is null)
        {
            CutNameText.Text = "等待中…";
            SessionText.Text = "0:00";
            TotalText.Text = "—";
            MineText.Text = "0:00:00";
        }
        else
        {
            long total = cur.LastSeen > 0 ? cur.LastSeen : cur.Baseline + cur.Mine;
            CutNameText.Text = cur.Display;
            SessionText.Text = FmtShort(_tracker.SessionTime);
            TotalText.Text = Fmt(TimeSpan.FromMilliseconds(total));
            MineText.Text = Fmt(TimeSpan.FromMilliseconds(cur.Mine));
        }

        // 进度环：本次时间 / 提醒间隔（参照 Win11 时钟的专注会话）
        double intervalMin = Math.Max(5, _tracker.State.StandUpMinutes);
        double pct = _tracker.SessionTime.TotalMinutes / intervalMin * 100.0;
        SessionRing.Value = Math.Clamp(pct, 0, 100);

        // 前台作画软件指示（带 CSP 图标）
        string? app = _tracker.ForegroundPaintApp;
        if (app is null)
        {
            AppText.Text = "未在作画";
            AppText.Opacity = 0.6;
            AppIcon.Opacity = 0.35;
        }        else
        {
            AppText.Text = app == "CLIPStudioPaint" ? "CLIP STUDIO PAINT" : app;
            AppText.Opacity = 1.0;
            AppIcon.Opacity = 1.0;
        }
        DataPathText.Text = ((App)Application.Current).Store.Root;
        UpdateSoundLabel();

        UpdateCountdown();
        MaybeRefreshChart();

        // 状态行：区分「本次运行还没读到新保存」和「这张卡还没任何记录」
        string tail;
        if (_tracker.LastReadAt is { } t)
            tail = $"数据截至 {t:HH:mm:ss} 保存";
        else if (cur is not null && cur.LastSeen > 0)
            tail = "显示的是已有记录；下次保存后会更新";
        else
            tail = "等待第一次保存";

        string err = string.IsNullOrEmpty(_tracker.LastError) ? "" : $"　·　{_tracker.LastError}";
        StatusText.Text = tail + err
            + (cur is null && _tracker.State.WatchRoots.Count == 0
                ? "　·　先添加一个监视目录"
                : "");

        RefreshRoots();
        RefreshList();
    }

    private void RefreshRoots()
    {
        var roots = _tracker.State.WatchRoots;
        // 目录列表变化很少，只在数量或内容变了才重建
        if (_rootPaths.Count == roots.Count &&
            _rootPaths.SequenceEqual(roots, StringComparer.OrdinalIgnoreCase))
            return;

        _rootPaths.Clear();
        _rootPaths.AddRange(roots);
        RootList.ItemsSource = roots.Select(r => new RootRow(r)).ToList();
        NoRootText.Visibility = roots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshList()
    {
        var cuts = _tracker.State.Cuts
            .Where(kv => !kv.Value.Archived)
            .OrderByDescending(kv => kv.Value.LastActive)
            .ToList();

        bool sameOrder = cuts.Count == _rows.Count;
        if (sameOrder)
        {
            for (int i = 0; i < cuts.Count; i++)
            {
                if (!string.Equals(_rows[i].Path, cuts[i].Key, StringComparison.OrdinalIgnoreCase))
                {
                    sameOrder = false;
                    break;
                }
            }
        }

        if (sameOrder)
        {
            // 常见路径：只是时间在走 —— 原地更新，不动容器，不闪
            for (int i = 0; i < cuts.Count; i++)
            {
                _rows[i].Touch(cuts[i].Value.LastActive);
                _rows[i].Update(cuts[i].Value);
            }
        }
        else
        {
            // 结构变了（新增/移除/换序）才重建，尽量复用已有行对象
            var byPath = _rows.ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);
            var rebuilt = new List<CutRow>(cuts.Count);
            foreach (var kv in cuts)
            {
                if (byPath.TryGetValue(kv.Key, out var existing))
                {
                    existing.Touch(kv.Value.LastActive);
                    existing.Update(kv.Value);
                    rebuilt.Add(existing);
                }
                else
                {
                    rebuilt.Add(new CutRow(kv.Key, kv.Value));
                }
            }

            _suppressSelection = true;
            try
            {
                _rows.Clear();
                foreach (var r in rebuilt) _rows.Add(r);

                string? cur = _tracker.CurrentPath;
                CutList.SelectedItem = rebuilt.FirstOrDefault(r =>
                    string.Equals(r.Path, cur, StringComparison.OrdinalIgnoreCase));
            }
            finally { _suppressSelection = false; }
        }

        NoCutText.Visibility = cuts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListHeader.Text = cuts.Count == 0 ? "已登记的卡" : $"已登记的卡（{cuts.Count}）";
    }

    private static string Fmt(TimeSpan t)
        => $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";

    /// <summary>进度环中间的空间小，一小时以内只显示 分:秒。</summary>
    private static string FmtShort(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";

    /// <summary>把 ComboBox 选到对应分钟数的预设项。</summary>
    /// <summary>把 NumberBox 同步到指定间隔（程序化写入，不触发设置回写）。</summary>
    private void SetInterval(int mins)
    {
        mins = Math.Clamp(mins, MinInterval, MaxInterval);
        if (Math.Abs(IntervalBox.Value - mins) < 0.5) { _intervalReady = true; return; }

        bool prev = _suppressSettings;
        _suppressSettings = true;
        try { IntervalBox.Value = mins; }
        finally { _suppressSettings = prev; }

        _intervalReady = true;   // 真实值已写入，之后才允许用户改动回写设置
    }
}
