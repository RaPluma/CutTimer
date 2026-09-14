using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace CutTimer.Core;

/// <summary>
/// Windows 通知（右下角那条）。
///
/// 用 Windows App SDK 的 AppNotificationManager，而不是托盘图标的 Shell_NotifyIcon 气泡：
/// 气泡在 Win10/11 上会被系统静默丢弃（专注助手、通知设置关掉、或者系统只认正式通知），
/// 而 AppNotificationManager 走的是和系统应用同一套通知管道，能进操作中心。
///
/// 非打包应用（WindowsPackageType=None）从 WinAppSDK 1.2 起支持直接 Register()。
/// 注册失败时 <see cref="IsAvailable"/> 为 false，调用方可以退回托盘气泡。
/// </summary>
public static class ToastNotifier
{
    private static bool _ready;
    private static Action? _onActivated;

    /// <summary>Windows 通知是否可用（注册成功）。</summary>
    public static bool IsAvailable => _ready;

    /// <summary>
    /// 注册通知通道。<paramref name="onActivated"/> 在用户**点击**通知时调用。
    ///
    /// 注意：系统不会通知「用户把通知划掉/忽略了」——只有点击才会回调。
    /// 所以「响到关掉通知」的停止条件实际是：点击通知 / 操作窗口 / 点重置。
    /// </summary>
    public static void Initialize(Action onActivated)
    {
        _onActivated = onActivated;
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, _) =>
            {
                try { _onActivated?.Invoke(); } catch { /* 忽略 */ }
            };
            AppNotificationManager.Default.Register();
            _ready = true;
        }
        catch
        {
            // 没注册上不是致命问题：调用方会退回托盘气泡
            _ready = false;
        }
    }

    /// <summary>弹一条通知。返回是否成功发出去。</summary>
    public static bool Show(string title, string body)
    {
        if (!_ready) return false;
        try
        {
            AppNotification notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>进程退出前注销。</summary>
    public static void Shutdown()
    {
        try { if (_ready) AppNotificationManager.Default.Unregister(); } catch { /* 忽略 */ }
        _ready = false;
    }
}
