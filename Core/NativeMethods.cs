using System.Runtime.InteropServices;
using System.Text;

namespace CutTimer.Core;

/// <summary>
/// Win32 互操作。集中在这里是为了让工程保持「纯 WinUI 3 + 少量 P/Invoke」，
/// 避免引入 WinForms（会把 WPF 的 XAML 编译器一起拖进来，与 WinUI 冲突）。
/// </summary>
internal static class NativeMethods
{
    // ---------------- 前台窗口 / 空闲检测 ----------------

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);

    /// <summary>返回当前前台窗口所属进程名（如 "CLIPStudioPaint"）；取不到返回 null。</summary>
    public static string? ForegroundProcessName()
    {
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            _ = GetWindowThreadProcessId(h, out int pid);
            if (pid <= 0) return null;
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return null;   // 进程可能刚好退出
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>距上次键鼠输入经过的时长（系统全局）。</summary>
    public static TimeSpan IdleTime()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return TimeSpan.Zero;
        uint now = unchecked((uint)Environment.TickCount);
        return TimeSpan.FromMilliseconds(unchecked(now - lii.dwTime));
    }

    // ---------------- 托盘图标 + 气泡通知 ----------------

    public const int WM_USER = 0x0400;
    public const int WM_TRAYICON = WM_USER + 1;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_COMMAND = 0x0111;
    public const int WM_DESTROY = 0x0002;

    public const int NIM_ADD = 0x00000000;
    public const int NIM_MODIFY = 0x00000001;
    public const int NIM_DELETE = 0x00000002;

    public const int NIF_MESSAGE = 0x00000001;
    public const int NIF_ICON = 0x00000002;
    public const int NIF_TIP = 0x00000004;
    public const int NIF_INFO = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;

        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATA lpData);

    public static bool TrayAdd(ref NOTIFYICONDATA data) => Shell_NotifyIconW(NIM_ADD, ref data);
    public static bool TrayModify(ref NOTIFYICONDATA data) => Shell_NotifyIconW(NIM_MODIFY, ref data);
    public static bool TrayDelete(ref NOTIFYICONDATA data) => Shell_NotifyIconW(NIM_DELETE, ref data);

    public static NOTIFYICONDATA NewTrayData(IntPtr hwnd) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = hwnd,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_TRAYICON,
        hIcon = IntPtr.Zero,
        szTip = "CutTimer",
        szInfo = "",
        szInfoTitle = "",
    };

    // ---------------- 消息窗口（托盘回调需要） ----------------

    public delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string? lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);

    // GetModuleHandleW 在 kernel32.dll，不在 user32.dll。
    // 写错 DLL 不会编译报错，只会在运行时抛 EntryPointNotFoundException ——
    // 而托盘初始化恰好把这个异常吞了，于是「托盘图标一直不存在」这件事
    // 谁也没发现。注册窗口类要用它拿 hInstance。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    // ---------------- 弹出菜单（托盘右键） ----------------

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenuW(IntPtr hMenu, int uFlags, IntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr hMenu, int fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public const int MF_STRING = 0x00000000;
    public const int MF_SEPARATOR = 0x00000800;
    public const int TPM_RETURNCMD = 0x0100;
    public const int TPM_RIGHTBUTTON = 0x0002;

    // ---------------- 无边框窗口拖动 ----------------

    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int HTCAPTION = 0x0002;

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_LBUTTON = 0x01;

    /// <summary>
    /// 物理左键此刻是否真的按下。
    ///
    /// 比起信任 PointerPressed/PointerReleased 的事件状态，直接问系统更可靠：
    /// 指针移出窗口、被别的元素抢走捕获等情况下 PointerReleased 会丢，
    /// 事件状态会一直停在"按下"，于是鼠标一划过窗口就误触发拖动。
    /// </summary>
    public static bool IsLeftButtonDown() => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

    /// <summary>取光标的物理像素坐标（与 AppWindow.Position 同一坐标系）。</summary>
    public static bool TryGetCursorPos(out int x, out int y)
    {
        if (GetCursorPos(out POINT p))
        {
            x = p.X;
            y = p.Y;
            return true;
        }
        x = 0;
        y = 0;
        return false;
    }

    // ---------------- 窗口枚举（单实例激活用） ----------------

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    /// <summary>取窗口标题；没有标题就返回空串。</summary>
    public static string GetWindowText(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return string.Empty;
        var sb = new System.Text.StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>这个窗口是不是被别的窗口拥有（IME / 弹出窗大多有 owner，顶层主窗口没有）。</summary>
    public static bool HasOwner(IntPtr hWnd) => GetWindow(hWnd, 4 /*GW_OWNER*/) != IntPtr.Zero;

    // ---------------- 提示音 ----------------

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySoundW(string? pszSound, IntPtr hmod, uint fdwSound);

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_ALIAS = 0x00010000;

    /// <summary>
    /// 播放一次系统提醒音。
    ///
    /// 用 winmm 的 PlaySound + 系统别名，而不是 System.Media.SystemSounds ——
    /// 后者在 System.Windows.Extensions 里，而这个项目早就把 WinForms/WPF 全关掉了。
    ///
    /// 别名选 Notification.Reminder 而不是经典的 SystemNotification：
    /// 实测本机的声音方案把 SystemNotification 映射到 Windows Background.wav
    /// （一段环境音，当提醒太弱），而 Notification.Reminder 对应真正的提示音。
    /// 别名解析不到时会退回系统默认提示音（没加 SND_NODEFAULT）。
    /// SND_ASYNC 保证不阻塞调用线程。
    /// </summary>
    public static void PlayNotificationSound()
    {
        try { _ = PlaySoundW("Notification.Reminder", IntPtr.Zero, SND_ALIAS | SND_ASYNC); }
        catch { /* 没声卡/被禁用时忽略，提醒本身不该因此失败 */ }
    }

    /// <summary>
    /// 循环播放提醒音，直到 <see cref="StopAlertLoop"/> 被调用。
    ///
    /// 优先播指定的 wav 文件（用户在设置里选的，或内置的 Assets\chime.wav）；
    /// 文件不存在就退回系统别名。SND_LOOP 必须和 SND_ASYNC 一起用。
    /// PlaySound 只认 wav，所以选择文件时也限定了 .wav。
    /// </summary>
    public static void StartAlertLoop(string? wavPath)
    {
        const uint SND_LOOP = 0x0008;
        const uint SND_FILENAME = 0x00020000;

        try
        {
            if (!string.IsNullOrEmpty(wavPath) && File.Exists(wavPath))
            {
                _ = PlaySoundW(wavPath, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_LOOP);
                return;
            }
            _ = PlaySoundW("Notification.Reminder", IntPtr.Zero, SND_ALIAS | SND_ASYNC | SND_LOOP);
        }
        catch { }
    }

    /// <summary>试听一次（不循环）。</summary>
    public static void PlayFileOnce(string? wavPath)
    {
        const uint SND_FILENAME = 0x00020000;
        try
        {
            if (!string.IsNullOrEmpty(wavPath) && File.Exists(wavPath))
                _ = PlaySoundW(wavPath, IntPtr.Zero, SND_FILENAME | SND_ASYNC);
            else
                PlayNotificationSound();
        }
        catch { }
    }

    /// <summary>停止循环播放（传 null 会让 winmm 掐掉当前正在播的声音）。</summary>
    public static void StopAlertLoop()
    {
        try { _ = PlaySoundW(null, IntPtr.Zero, 0); }
        catch { }
    }
}
