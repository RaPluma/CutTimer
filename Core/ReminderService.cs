using System.Diagnostics;

namespace CutTimer.Core;

/// <summary>提醒的三种状态。</summary>
public enum ReminderPhase
{
    /// <summary>正常计时中。</summary>
    Running,

    /// <summary>手动暂停，秒表停住。</summary>
    Paused,

    /// <summary>已经提醒过了，正等你手动重开（不会自动开始下一轮）。</summary>
    Fired,
}

/// <summary>
/// 起身 / 护眼提醒。
///
/// 关键点是**空闲检测**：如果你已经离开（无键鼠输入超过阈值），
/// 说明你本来就站起来了，不该再提醒"起来活动"。
/// 所以长时间空闲会把本次周期直接重置，回来重新计时。
///
/// 另一条规则是**到点后不自动复位**：响过之后停在 Fired，倒计时定在 0，
/// 必须手动「重置」才开始新的一轮。否则人不在电脑前会一直空响。
/// </summary>
public sealed class ReminderService : IDisposable
{
    private readonly CutTracker _tracker;
    private readonly System.Threading.Timer _timer;
    private readonly Stopwatch _sinceBreak = Stopwatch.StartNew();
    private readonly Stopwatch _sinceEye = Stopwatch.StartNew();
    private bool _idleReset;

    /// <summary>该起身活动了。</summary>
    public event Action<TimeSpan>? StandUpDue;

    /// <summary>该远眺护眼了（20-20-20）。</summary>
    public event Action? EyeRestDue;

    public TimeSpan SinceBreak => _sinceBreak.Elapsed;

    /// <summary>当前状态：计时中 / 暂停 / 已提醒待重开。</summary>
    public ReminderPhase Phase { get; private set; } = ReminderPhase.Running;

    /// <summary>距下次起身提醒还剩多久（到点返回 0）。</summary>
    public TimeSpan UntilStandUp
    {
        get
        {
            TimeSpan period = TimeSpan.FromMinutes(Math.Max(5, _tracker.State.StandUpMinutes));
            TimeSpan left = period - _sinceBreak.Elapsed;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>本周期已经过去的比例（0..1），用来画倒计时圆环。</summary>
    public double StandUpProgress
    {
        get
        {
            double period = Math.Max(5, _tracker.State.StandUpMinutes);
            double elapsed = _sinceBreak.Elapsed.TotalMinutes;
            return Math.Clamp(elapsed / period, 0, 1);
        }
    }

    private static void Log(string msg)
    {
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutTimer");
            Directory.CreateDirectory(root);
            File.AppendAllText(Path.Combine(root, "reminder.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    public ReminderService(CutTracker tracker)
    {
        _tracker = tracker;
        _timer = new System.Threading.Timer(_ => Check(), null,
            Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutTimer");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "reminder.log"), "");
        }
        catch { }
        Log($"Start: enabled={_tracker.State.ReminderEnabled} interval={_tracker.State.StandUpMinutes}min idle={_tracker.State.IdleMinutes}min");
        _timer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    /// <summary>手动重置：从「已提醒」或「暂停」回到正常计时，开始新的一轮。</summary>
    public void ResetBreak()
    {
        _sinceBreak.Restart();
        _sinceEye.Restart();
        _idleReset = false;
        Phase = ReminderPhase.Running;
        Log("cycle restarted manually");
    }

    /// <summary>倒计时是否被手动暂停。</summary>
    public bool IsPaused => Phase == ReminderPhase.Paused;
    /// <summary>是否处于「已经提醒过、等你手动重开」的状态。</summary>
    public bool HasFired => Phase == ReminderPhase.Fired;

    /// <summary>
    /// 暂停 / 继续倒计时。
    /// 用 Stopwatch.Stop()/Start()，继续时从暂停处接着走，不会把暂停的时间算进去。
    /// </summary>
    public void SetPaused(bool paused)
    {
        if (paused)
        {
            if (Phase == ReminderPhase.Paused) return;
            Phase = ReminderPhase.Paused;
            _sinceBreak.Stop();
            _sinceEye.Stop();
            Log("paused");
        }
        else
        {
            if (Phase != ReminderPhase.Paused) return;
            Phase = ReminderPhase.Running;
            _sinceBreak.Start();
            _sinceEye.Start();
            Log("resumed");
        }
    }

    private void Check()
    {
        try
        {
            AppState s = _tracker.State;
            if (!s.ReminderEnabled) return;
            if (Phase != ReminderPhase.Running) return;   // 暂停中 / 已提醒待手动重开：什么都不做

            TimeSpan idle = NativeMethods.IdleTime();

            // 离开够久 = 已经休息过了 → 重置周期，回来重新算
            if (idle >= TimeSpan.FromMinutes(Math.Max(1, s.IdleMinutes)))
            {
                if (!_idleReset)
                {
                    Log($"idle {idle.TotalMinutes:0.#}min >= {s.IdleMinutes}min -> reset cycle (你已离开，当作休息过了)");
                    _sinceBreak.Restart();
                    _sinceEye.Restart();
                    _idleReset = true;
                }
                return;
            }
            _idleReset = false;

            if (_sinceBreak.Elapsed >= TimeSpan.FromMinutes(Math.Max(5, s.StandUpMinutes)))
            {
                TimeSpan span = TimeSpan.FromMinutes(s.StandUpMinutes);

                // 关键：**不自动开始下一轮**。
                // 停表并进入 Fired，倒计时定在 0，等你手动「重置」才重新走。
                // 早先这里是 _sinceBreak.Restart()，也就是每 30 分钟无条件再响一次，
                // 人不在电脑前就会一直空响。
                _sinceBreak.Stop();
                _sinceEye.Stop();
                Phase = ReminderPhase.Fired;
                Log($"FIRED stand-up reminder (after {span.TotalMinutes:0.#} min) -> waiting for manual restart");
                StandUpDue?.Invoke(span);
            }

            if (s.EyeRestEnabled && _sinceEye.Elapsed >= TimeSpan.FromMinutes(Math.Max(5, s.EyeRestMinutes)))
            {
                _sinceEye.Restart();
                Log($"FIRED eye-rest reminder (after {s.EyeRestMinutes} min)");
                EyeRestDue?.Invoke();
            }
        }
        catch
        {
            // 提醒失败不应影响计时
        }
    }

    public void Dispose() => _timer.Dispose();
}
