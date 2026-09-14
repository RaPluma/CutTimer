using CutTimer.Core;

namespace CutTimer;

/// <summary>
/// 自定义入口点（替代 XAML 生成的 Main），以便在启动界面之前处理命令行参数。
/// 需要在 csproj 里定义 DISABLE_XAML_GENERATED_MAIN。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 无界面诊断模式：CutTimer.exe probe <file.clip> [more.clip ...]
        if (args.Length >= 2 && args[0] == "--probe")
        {
            return Probe(args[1..]);
        }

        // 诊断：列出目标进程当前打开的 .clip
        if (args.Length >= 1 && args[0] == "--openfiles")
        {
            string procName = args.Length >= 2 ? args[1] : "CLIPStudioPaint";
            return OpenFiles(procName);
        }

        // 单实例保护。
        // 两个实例会同时监视同一批目录、各自持有一份 State，然后互相覆盖
        // state.json（最后写盘的赢）—— 这会真的丢掉已累计的作业时间。
        // 用命名 Mutex：第二个实例直接把已有窗口叫到前面然后退出。
        using var mutex = new System.Threading.Mutex(true, @"Local\CutTimer.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            ActivateExistingWindow();
            return 0;
        }

        Microsoft.UI.Xaml.Application.Start(p =>
        {
            _ = new App();
        });
        return 0;
    }

    /// <summary>主窗口标题（MainWindow 里设的），用来和悬浮小窗区分开。</summary>
    private const string MainWindowTitle = "CutTimer";

    /// <summary>
    /// 把已在运行的实例叫到前面。
    ///
    /// 这里**不能只找「可见」窗口** —— 主窗口点 X 之后是隐藏到托盘（不是退出），
    /// 那时全进程没有一个可见窗口，就会什么都不做然后退出，
    /// 用户双击图标看起来是「完全没反应」，以为程序坏了。
    ///
    /// 所以分两轮：
    ///   1. 有可见窗口 → 直接置前（正常情况）
    ///   2. 一个可见的都没有 → 找到主窗口把它显示出来再置前
    ///
    /// 显示必须用 SW_SHOWNORMAL：对「被隐藏」的窗口调 SW_RESTORE 会把它显示出来、
    /// 但停在最小化状态（实测 visible=True 却 iconic=True，159x27），用户还是看不到。
    /// </summary>
    private static void ActivateExistingWindow()
    {
        var trace = new List<string>();
        try
        {
            var others = System.Diagnostics.Process.GetProcessesByName("CutTimer")
                               .Select(p => (uint)p.Id).ToArray();
            var cutTimerPids = new HashSet<uint>(others);
            trace.Add($"self={Environment.ProcessId} others=[{string.Join(",", others)}]");

            IntPtr visible = IntPtr.Zero;
            IntPtr mainHidden = IntPtr.Zero;
            IntPtr anyHidden = IntPtr.Zero;

            NativeMethods.EnumWindows((h, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(h, out uint pid);
                if (!cutTimerPids.Contains(pid)) return true;

                string title = NativeMethods.GetWindowText(h);
                bool vis = NativeMethods.IsWindowVisible(h);
                trace.Add($"  hwnd={h} pid={pid} vis={vis} owner={NativeMethods.HasOwner(h)} title='{title}'");

                if (title.Length == 0) return true;      // 跳过 IME / DDE 之类的辅助窗口

                if (vis)
                {
                    if (visible == IntPtr.Zero) visible = h;
                }
                else
                {
                    if (anyHidden == IntPtr.Zero) anyHidden = h;
                    if (mainHidden == IntPtr.Zero && title == MainWindowTitle) mainHidden = h;
                }
                return true;
            }, IntPtr.Zero);

            // 优先主窗口：用户点图标的本意就是「把程序打开」
            IntPtr target = visible != IntPtr.Zero ? visible
                          : mainHidden != IntPtr.Zero ? mainHidden
                          : anyHidden;
            trace.Add($"picked target={target} (visible={visible} mainHidden={mainHidden} anyHidden={anyHidden})");
            if (target == IntPtr.Zero) { WriteTrace(trace); return; }

            NativeMethods.ShowWindow(target, 1);            // SW_SHOWNORMAL
            NativeMethods.SetForegroundWindow(target);
            trace.Add($"after ShowWindow visible={NativeMethods.IsWindowVisible(target)} " +
                      $"iconic={NativeMethods.IsIconic(target)}");
        }
        catch (Exception ex)
        {
            trace.Add("EXCEPTION: " + ex);
        }
        WriteTrace(trace);
    }

    /// <summary>
    /// 记下「第二个实例为什么没叫醒窗口」。光看「双击没反应」是猜不出来的。
    /// 只在第二个实例启动时写一次，失败就算了。
    /// </summary>
    private static void WriteTrace(List<string> lines)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutTimer");
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "single-instance.log"),
                new[] { DateTime.Now.ToString("HH:mm:ss.fff") }.Concat(lines));
        }
        catch { /* 忽略 */ }
    }

    private static int OpenFiles(string procName)
    {
        var procs = System.Diagnostics.Process.GetProcessesByName(procName);
        if (procs.Length == 0)
        {
            Console.WriteLine($"进程未运行: {procName}");
            return 1;
        }

        int total = 0;
        foreach (var p in procs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            List<string> files = OpenFileProbe.Scan(p.Id, out var d);
            sw.Stop();

            Console.WriteLine($"PID {p.Id} ({procName})  耗时 {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"  全系统句柄={d.TotalHandles}  该进程持有={d.OwnedHandles}  File 类型={d.FileHandles}");
            Console.WriteLine($"  File 类型索引={d.FileTypeIndex}  Duplicate失败={d.DupFailed}  取路径失败={d.PathFailed}");
            if (d.Note is not null) Console.WriteLine($"  备注: {d.Note}");
            Console.WriteLine($"  找到 .clip: {files.Count}");
            foreach (string f in files) Console.WriteLine("    " + f);

            if (d.SamplePaths.Count > 0)
            {
                Console.WriteLine($"  该进程打开的文件样例（前 15 个）:");
                foreach (string s in d.SamplePaths.Take(15)) Console.WriteLine("    " + s);
            }
            total += files.Count;
        }
        return total > 0 ? 0 : 2;
    }

    private static int Probe(string[] files)
    {
        int failed = 0;
        foreach (string f in files)
        {
            ClipReading r = ClipWorkTime.Read(f);
            if (r.Ok)
            {
                Console.WriteLine($"OK\t{r.WorkTimeMs}\t{Format(r.WorkTimeMs)}\t{r.SettingType}\t{r.ProjectName}\t{Path.GetFileName(f)}");
            }
            else
            {
                failed++;
                Console.WriteLine($"FAIL\t{r.Note}\t\t\t\t{Path.GetFileName(f)}");
            }
        }
        return failed;
    }

    private static string Format(long ms)
    {
        long s = ms / 1000;
        return $"{s / 3600}:{s % 3600 / 60:D2}:{s % 60:D2}";
    }
}
