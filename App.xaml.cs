using System.Diagnostics;
using CutTimer.Core;
using Microsoft.UI.Xaml;

namespace CutTimer;

public partial class App : Application
{
    public static App Current2 => (App)Current;

    public StateStore Store { get; private set; } = null!;
    public CutTracker Tracker { get; private set; } = null!;
    public ReminderService Reminders { get; private set; } = null!;
    public TrayIcon? Tray { get; private set; }

    private MainWindow? _window;
    private MiniWindow? _mini;

    /// <summary>悬浮小窗当前是否可见（主界面用它同步按钮状态）。</summary>
    public bool MiniVisible => _mini is not null;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Store = new StateStore();
        Tracker = new CutTracker(Store);
        Reminders = new ReminderService(Tracker);

        _window = new MainWindow(Tracker);
        _window.Activate();
        _window.LoadStartupFiles();   // 必须在 Activate 之后，见方法注释

        // 点主窗口的 ✕ 不退出程序，收进托盘（否则窗口被销毁后再也放不出来）
        try
        {
            _window.AppWindow.Closing += (_, e) =>
            {
                e.Cancel = true;
                Log("main window closing -> hide to tray");
                HideMain();
            };
        }
        catch (Exception ex) { Log("hook main closing failed: " + ex.Message); }

        // 用户回来操作窗口 = 已经看到提醒了，把循环音效停掉
        // （系统不会回调「通知被划掉」，所以这是除点通知/点重置之外唯一的停止时机）
        try { _window.Activated += (_, _) => AcknowledgeAlert(); } catch { }

        SetupTray();

        // 注册 Windows 通知通道（失败会自动退回托盘气泡）
        ToastNotifier.Initialize(OnNotificationActivated);
        Log($"windows toast available = {ToastNotifier.IsAvailable}");

        // 必须用事件带出来的 span：Check() 在触发事件前就把秒表停了，
        // 这时再读 Reminders.SinceBreak 只会拿到 0，通知文案会变成「连续作画 0 分钟」。
        Reminders.StandUpDue += span => FireStandUpReminder(span);

        Reminders.EyeRestDue += () =>
        {
            NativeMethods.PlayNotificationSound();
            if (!ToastNotifier.Show("远眺一下", "看看 6 米外的东西，20 秒就好。"))
                Tray?.Notify("远眺一下", "看看 6 米外的东西，20 秒就好。");
        };
        Tracker.Start();
        Reminders.Start();

        // 上次退出时小窗是开着的，就恢复它
        if (Tracker.State.MiniWindowEnabled) ShowMini();
    }

    // ---------------- 图标 ----------------

    /// <summary>
    /// 给窗口设图标（标题栏左上角 + 任务栏 + Alt-Tab）。
    ///
    /// WinUI 3 **不会**自动继承 exe 的图标，必须显式设 ——
    /// 不设的话标题栏和任务栏会是个空白默认图标。
    /// 托盘图标不走这里：它是从 exe 里 ExtractIconW 取的，设了 csproj 的
    /// ApplicationIcon 就自动有了。
    /// </summary>
    public static void ApplyWindowIcon(Window window)
    {
        try
        {
            string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "CutTimer.ico");
            if (File.Exists(ico)) window.AppWindow.SetIcon(ico);
        }
        catch { /* 设不上就保持系统默认，不该影响启动 */ }
    }

    // ---------------- 提醒 ----------------

    private bool _alertRinging;

    /// <summary>提醒音是否正在循环播放。</summary>
    public bool AlertRinging => _alertRinging;

    /// <summary>
    /// 版本号，来自 csproj 的 &lt;Version&gt;。
    /// 用户看到的「1.0.1」就是这里来的 —— 发版只要改 csproj 一处。
    /// </summary>
    public static string Version { get; } =
        (System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute info, ..])
            ? info.InformationalVersion.Split('+')[0]      // 去掉 +<git sha> 后缀
            : "0.0.0";

    /// <summary>内置默认铃声（生成的 Assets\chime.wav）。</summary>
    public static string DefaultChimePath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "chime.wav");

    /// <summary>
    /// Assets\csp.png 在不在。
    /// 它是 CLIP STUDIO PAINT 的官方图标，版权属 CELSYS，所以不进仓库 ——
    /// 改由构建前 tools/extract-csp-icon.ps1 从本机装的 CSP 里提取。
    /// 本机没装 CSP 时这个文件就不存在，界面要把图标位收起来，只留文字。
    /// </summary>
    public static bool HasCspIcon { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "csp.png"));

    /// <summary>没装 CSP 就把图标收起来，免得留一块空白（<paramref name="icon"/> 可以是 null）。</summary>
    public static void FitAppIcon(FrameworkElement? icon)
    {
        if (icon is null) return;
        icon.Visibility = HasCspIcon ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 当前实际用于提醒的 wav；没有就用内置的。
    /// 返回 null 表示「让 winmm 走系统别名」。
    /// </summary>
    public string? AlertSoundPath
    {
        get
        {
            string? custom = Tracker.State.AlertSoundPath;
            if (string.IsNullOrWhiteSpace(custom)) return DefaultChimePath;
            return custom;
        }
    }

    /// <summary>给界面显示用的音效名。</summary>
    public string AlertSoundLabel
    {
        get
        {
            string? custom = Tracker.State.AlertSoundPath;
            if (string.IsNullOrWhiteSpace(custom)) return "默认铃声";
            return Path.GetFileName(custom);
        }
    }

    /// <summary>
    /// 弹一条起身提醒：右下角 Windows 通知 + 循环提示音。
    ///
    /// 音效是**循环**的，响到用户对提醒做出反应为止（点通知 / 操作窗口 / 点重置）。
    /// 系统不会回调「通知被划掉」，所以停止条件只能是这些主动操作。
    /// </summary>
    public void FireStandUpReminder(TimeSpan worked)
    {
        string title = "该起来活动了";
        string body = $"已经连续作画 {worked.TotalMinutes:0} 分钟。站起来走走、伸展一下肩颈。";

        NativeMethods.StartAlertLoop(AlertSoundPath);
        _alertRinging = true;

        // 优先用 Windows 通知（会进操作中心、在右下角弹出）；
        // 注册失败才退回托盘气泡。
        bool shown = ToastNotifier.Show(title, body);
        if (!shown) Tray?.Notify(title, body, warn: true);

        Log($"reminder fired: sound loop ({AlertSoundLabel}) + {(shown ? "Windows toast" : "tray balloon")} (after {worked.TotalMinutes:0.#} min)");
    }

    /// <summary>马上试听当前音效（只响一次，不循环）。</summary>
    public void PreviewAlertSound() => NativeMethods.PlayFileOnce(AlertSoundPath);

    /// <summary>用户对提醒做出了反应 —— 停掉循环音效。</summary>
    public void AcknowledgeAlert()
    {
        if (!_alertRinging) return;
        _alertRinging = false;
        NativeMethods.StopAlertLoop();
        Log("alert acknowledged: sound stopped");
    }

    /// <summary>用户点了通知：停声音，并开始新的一轮计时。</summary>
    private void OnNotificationActivated()
    {
        AcknowledgeAlert();
        Reminders.ResetBreak();
        ShowWindow();
        Log("notification clicked -> sound stopped + cycle restarted");
    }

    // ---------------- 悬浮小窗 ----------------

    private static void Log(string msg)
    {
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutTimer");
            Directory.CreateDirectory(root);
            File.AppendAllText(Path.Combine(root, "mini.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    public void ToggleMiniWindow() => SetMiniVisible(!MiniVisible);

    /// <summary>
    /// 主窗口与小窗**互斥**：开小窗就把主窗口收起来，关小窗就把主窗口放回来。
    /// </summary>
    public void SetMiniVisible(bool visible)
    {
        if (visible) ShowMini(); else SwitchToMain();
    }

    private void ShowMini()
    {
        Log($"ShowMini: MiniVisible={MiniVisible}");
        if (_mini is null)
        {
            try
            {
                _mini = new MiniWindow(Tracker);
                _mini.Activated += (_, _) => AcknowledgeAlert();
                _mini.Closed += (_, _) =>
                {
                    // SwitchToMain 会先把 _mini 置空再 Close；
                    // 所以这里若 _mini 仍非空，说明是用户自己关的（✕ / Alt+F4 / 系统关闭），
                    // 应该切回主窗口，否则会变成"两个窗口都没了"。
                    bool userClosed = _mini is not null;
                    Log($"MiniWindow.Closed fired (userClosed={userClosed})");
                    _mini = null;
                    if (Tray is not null) Tray.MiniWindowVisible = false;
                    if (userClosed) SwitchToMain();
                };
            }
            catch (Exception ex)
            {
                Log("MiniWindow ctor FAILED: " + ex);
                _mini = null;
                return;
            }
        }

        try { _mini.Activate(); } catch (Exception ex) { Log("Activate failed: " + ex.Message); }

        // 互斥：小窗出来，主窗口就收起来
        HideMain();

        if (Tray is not null) Tray.MiniWindowVisible = true;
        Tracker.UpdateSettings(s => s.MiniWindowEnabled = true);
        Log("ShowMini done (main hidden)");
    }

    /// <summary>切回主窗口模式：关掉小窗、把主窗口放回来。</summary>
    public void SwitchToMain()
    {
        Log("SwitchToMain");
        MiniWindow? m = _mini;
        _mini = null;    // 先清引用：Closed 回调据此判断"这是程序主动关的，不用再切"
        if (Tray is not null) Tray.MiniWindowVisible = false;
        try { m?.Close(); } catch { /* 忽略 */ }

        ShowMain();
        Tracker.UpdateSettings(s => s.MiniWindowEnabled = false);
    }

    private void ShowMain()
    {
        if (_window is null) return;
        try { _window.AppWindow.Show(); } catch { /* 忽略 */ }
        try { _window.Activate(); } catch { /* 忽略 */ }
    }

    private void HideMain()
    {
        try { _window?.AppWindow.Hide(); } catch { /* 忽略 */ }
    }

    private void SetupTray()
    {
        try
        {
            Tray = new TrayIcon();
            Tray.ShowRequested += ShowWindow;
            Tray.CommandInvoked += OnTrayCommand;
            UpdateTrayTooltip();
            Tracker.Changed += UpdateTrayTooltip;

            // 托盘图标放进去了没有？Win11 默认把它收进溢出区（^ 里），
            // 用户看不到就容易以为程序没在跑 —— 记一行方便排查。
            Log($"tray icon: added={Tray.Added} hwnd=0x{Tray.WindowHandle:X} " +
                $"icon=0x{Tray.IconHandle:X} (Win11 默认收进溢出区)");
        }
        catch (Exception ex)
        {
            // 托盘失败不应阻止主程序可用 —— 但一定要落盘，
            // 否则「托盘图标不见了」这种事完全无从查起。
            Debug.WriteLine("tray init failed: " + ex);
            Log("tray init FAILED: " + ex);
        }
    }

    /// <summary>托盘的「显示窗口」—— 切回主窗口模式（会顺带关掉小窗）。</summary>
    private void ShowWindow() => SwitchToMain();

    private void OnTrayCommand(int cmd)
    {
        switch (cmd)
        {
            case TrayIcon.CmdShow:
                ShowWindow();
                break;

            case TrayIcon.CmdReset:
                if (Tracker.CurrentPath is { } p)
                {
                    Tracker.ResetBaseline(p);
                    Tray?.Notify("已重置基线", $"{Path.GetFileName(p)} 从现在起算作你的时间。");
                }
                break;

            case TrayIcon.CmdToggleReminder:
                Tracker.UpdateSettings(s => s.ReminderEnabled = !s.ReminderEnabled);
                Tray?.Notify("起身提醒", Tracker.State.ReminderEnabled ? "已开启" : "已关闭");
                break;

            case TrayIcon.CmdToggleMini:
                ToggleMiniWindow();
                break;

            case TrayIcon.CmdOpenData:
                try { Process.Start("explorer.exe", Store.Root); } catch { /* 忽略 */ }
                break;

            case TrayIcon.CmdExit:
                Shutdown();
                break;
        }
    }

    private void UpdateTrayTooltip()
    {
        if (Tray is null) return;
        CutState? cur = Tracker.Current;
        Tray.UpdateTooltip(cur is null
            ? "CutTimer — 还没有登记任何卡"
            : $"CutTimer — {cur.Display}\n你的 {Format(cur.Mine)}  /  本卡 {Format(cur.Baseline + cur.Mine)}");
    }

    private static string Format(long ms)
    {
        long s = ms / 1000;
        return $"{s / 3600}:{s % 3600 / 60:D2}:{s % 60:D2}";
    }

    public void Shutdown()
    {
        NativeMethods.StopAlertLoop();          // 别让循环音效活过进程
        try { Tracker?.Stop(); Tracker?.Dispose(); } catch { }
        try { Reminders?.Dispose(); } catch { }
        try { ToastNotifier.Shutdown(); } catch { }
        try { Tray?.Dispose(); } catch { }
        try { _mini?.Close(); } catch { }
        _window?.Close();
        Exit();
    }
}
