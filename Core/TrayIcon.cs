using System.Runtime.InteropServices;

namespace CutTimer.Core;

/// <summary>
/// 系统托盘图标。用纯 Win32 Shell_NotifyIcon 实现，不用 WinForms
/// （UseWindowsForms 会导入 WPF 的 XAML 编译器，与 WinUI 冲突且压不住）。
///
/// 结构：注册一个消息窗口类 → 创建 message-only 窗口 → 挂 Shell_NotifyIcon，
/// 回调消息里处理左键（显示主窗）与右键（弹出菜单）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly NativeMethods.WndProcDelegate _wndProc;   // 必须持有引用，否则会被 GC 回收
    private NativeMethods.NOTIFYICONDATA _data;
    private bool _added;

    // 菜单项 ID
    public const int CmdShow = 100;
    public const int CmdReset = 101;
    public const int CmdPauseResume = 102;
    public const int CmdToggleReminder = 103;
    public const int CmdOpenData = 104;
    public const int CmdToggleMini = 105;
    public const int CmdExit = 199;

    public event Action? ShowRequested;
    public event Action<int>? CommandInvoked;

    /// <summary>菜单里显示的提示文字（如当前卡名），由宿主更新。</summary>
    public string StatusLine { get; set; } = "CutTimer";

    /// <summary>悬浮小窗当前是否可见，决定菜单显示「显示」还是「隐藏」。</summary>
    public bool MiniWindowVisible { get; set; }

    /// <summary>Shell_NotifyIcon(NIM_ADD) 是否成功。</summary>
    public bool Added => _added;

    /// <summary>托盘的回调隐藏窗口（诊断用）。</summary>
    public IntPtr WindowHandle => _hwnd;

    /// <summary>实际挂上去的图标句柄；0 表示退回了系统默认图标（诊断用）。</summary>
    public IntPtr IconHandle => _data.hIcon;

    public TrayIcon()
    {
        _wndProc = WndProc;
        IntPtr hInst = NativeMethods.GetModuleHandleW(null);

        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInst,
            lpszClassName = "CutTimerTrayWnd",
        };
        _ = NativeMethods.RegisterClassExW(ref wc);

        _hwnd = NativeMethods.CreateWindowExW(0, "CutTimerTrayWnd", "CutTimer",
            0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);

        _data = NativeMethods.NewTrayData(_hwnd);
        _data.hIcon = LoadAppIcon();
        _data.szTip = Truncate("CutTimer", 127);
        _added = NativeMethods.TrayAdd(ref _data);
    }

    private static IntPtr LoadAppIcon()
    {
        // 直接复用 exe 自身的图标，避免额外资源依赖
        try
        {
            string exe = Environment.ProcessPath ?? "";
            IntPtr h = LoadIconFromFile(exe);
            if (h != IntPtr.Zero) return h;
        }
        catch { /* 落到默认 */ }
        return LoadIcon(IntPtr.Zero, 32512);   // IDI_APPLICATION
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    private static IntPtr LoadIcon(IntPtr hInstance, int id) => LoadIconW(hInstance, new IntPtr(id));

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIconW(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    private static IntPtr LoadIconFromFile(string path)
    {
        IntPtr h = ExtractIconW(IntPtr.Zero, path, 0);
        return h == new IntPtr(1) ? IntPtr.Zero : h;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];

    /// <summary>更新托盘提示文字。</summary>
    public void UpdateTooltip(string text)
    {
        if (!_added) return;
        StatusLine = text;
        _data.szTip = Truncate(text, 127);
        _data.uFlags = NativeMethods.NIF_TIP;
        _ = NativeMethods.TrayModify(ref _data);
        _data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;
    }

    /// <summary>弹一个气泡通知（Win10/11 上呈现为系统通知）。</summary>
    public void Notify(string title, string message, bool warn = false)
    {
        if (!_added) return;
        _data.uFlags = NativeMethods.NIF_INFO;
        _data.szInfoTitle = Truncate(title, 63);
        _data.szInfo = Truncate(message, 255);
        _data.dwInfoFlags = warn ? 0x00000002 : 0x00000001;   // 2=Warning 1=Info
        _data.uTimeoutOrVersion = 10000;
        _ = NativeMethods.TrayModify(ref _data);
        _data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;
    }

    private IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_TRAYICON)
        {
            int evt = lParam.ToInt32() & 0xFFFF;
            if (evt == NativeMethods.WM_LBUTTONUP)
            {
                ShowRequested?.Invoke();
            }
            else if (evt == NativeMethods.WM_RBUTTONUP)
            {
                ShowMenu();
            }
            return IntPtr.Zero;
        }
        if (msg == NativeMethods.WM_DESTROY)
        {
            if (_added) { NativeMethods.TrayDelete(ref _data); _added = false; }
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, new IntPtr(CmdShow), "显示窗口");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, new IntPtr(CmdToggleMini),
                MiniWindowVisible ? "隐藏悬浮小窗" : "显示悬浮小窗");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, "");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, new IntPtr(CmdReset), "重置基线（从此算我的）");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, new IntPtr(CmdToggleReminder), "开关起身提醒");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, "");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, new IntPtr(CmdOpenData), "打开数据目录");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, new IntPtr(CmdExit), "退出");

            NativeMethods.GetCursorPos(out var pt);
            // TrackPopupMenu 需要前台窗口，否则点击别处菜单不消失
            NativeMethods.SetForegroundWindow(_hwnd);
            int cmd = NativeMethods.TrackPopupMenuEx(menu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
                pt.X, pt.Y, _hwnd, IntPtr.Zero);
            if (cmd != 0) CommandInvoked?.Invoke(cmd);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            NativeMethods.TrayDelete(ref _data);
            _added = false;
        }
        if (_hwnd != IntPtr.Zero) NativeMethods.DestroyWindow(_hwnd);
    }
}
