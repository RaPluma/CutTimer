using System.Runtime.InteropServices;
using System.Text;

namespace CutTimer.Core;

/// <summary>
/// 找出某个进程当前打开着的 .clip 文件。
///
/// 为什么需要它：CSP 的窗口标题只有「CLIP STUDIO PAINT」，不含文档名；
/// 它也不给 .clip 加独占锁；而且只在保存时才写盘。所以「你现在在画哪一卡」
/// 唯一可靠的来源是——**CSP 进程持有的文件句柄**。
///
/// 做法（进程管理器的通用套路）：
///   1. NtQuerySystemInformation(SystemExtendedHandleInformation) 取全系统句柄快照
///   2. 先用自己打开的一个已知文件，从快照里定位 "File" 类型的 ObjectTypeIndex
///      （这样就不必对任意句柄调用 NtQueryObject —— 那个调用在某些句柄上会挂住）
///   3. 对目标进程中类型为 File 的句柄做 DuplicateHandle，再用
///      GetFinalPathNameByHandle 拿到路径
///   4. 过滤出 .clip
///
/// 只需要 PROCESS_DUP_HANDLE 权限，同用户进程默认可得，不需要管理员。
/// </summary>
public static class OpenFileProbe
{
    private const int SystemExtendedHandleInformation = 64;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const int DUPLICATE_SAME_ACCESS = 0x00000002;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr srcProc, IntPtr srcHandle, IntPtr dstProc,
                                               out IntPtr dstHandle, uint access, bool inherit, uint options);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetFinalPathNameByHandleW(IntPtr h, StringBuilder buf, int len, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa,
                                             uint disposition, uint flags, IntPtr template);

    // SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX 的字段偏移（x64，共 40 字节）：
    //   Object                @ 0
    //   UniqueProcessId       @ 8
    //   HandleValue           @ 16
    //   GrantedAccess         @ 24
    //   CreatorBackTraceIndex @ 28
    //   ObjectTypeIndex       @ 30
    //   HandleAttributes      @ 32
    //   Reserved              @ 36
    private const int OffPid = 8;
    private const int OffHandle = 16;
    private const int OffTypeIndex = 30;
    private const int HandleEntrySize = 40;

    /// <summary>
    /// 返回该进程当前打开着的 .clip 路径（已归一化）。取不到返回空集合。
    /// 注意：这是重型操作（要取全系统句柄快照），不要高频调用。
    /// </summary>
    public static List<string> FindOpenClips(int pid) => Scan(pid, out _);

    /// <summary>带诊断信息的版本，供 --openfiles 使用。</summary>
    public static List<string> Scan(int pid, out ScanDiagnostics diag)
    {
        var d = new ScanDiagnostics();
        diag = d;
        var result = new List<string>();

        IntPtr snapshot = IntPtr.Zero;
        IntPtr probeHandle = IntPtr.Zero;
        string probePath = Path.Combine(Path.GetTempPath(), $"cuttimer_probe_{Environment.ProcessId}.tmp");
        try
        {
            // 关键顺序：必须先打开探针文件，再取快照，
            // 否则快照里没有这个句柄，就查不到 File 的类型索引。
            try
            {
                File.WriteAllText(probePath, "x");
                // GENERIC_READ, FILE_SHARE_READ|WRITE, OPEN_EXISTING, FILE_ATTRIBUTE_TEMPORARY
                probeHandle = CreateFileW(probePath, 0x80000000, 3, IntPtr.Zero, 3, 0x100, IntPtr.Zero);
                if (probeHandle == new IntPtr(-1)) probeHandle = IntPtr.Zero;
            }
            catch { probeHandle = IntPtr.Zero; }

            snapshot = TakeSnapshot(out long handleCount);
            d.TotalHandles = handleCount;
            if (snapshot == IntPtr.Zero) { d.Note = "snapshot failed"; return result; }

            int fileTypeIndex = probeHandle != IntPtr.Zero
                ? FindFileTypeIndex(snapshot, handleCount, probeHandle)
                : -1;
            d.FileTypeIndex = fileTypeIndex;
            if (fileTypeIndex < 0) { d.Note = "File type index not found"; return result; }

            IntPtr target = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
            if (target == IntPtr.Zero)
            {
                d.Note = $"OpenProcess failed, err={Marshal.GetLastWin32Error()}";
                return result;
            }

            try
            {
                IntPtr self = System.Diagnostics.Process.GetCurrentProcess().Handle;
                long baseAddr = snapshot.ToInt64() + 16;

                for (long i = 0; i < handleCount; i++)
                {
                    long e = baseAddr + i * HandleEntrySize;
                    if (Marshal.ReadInt32(new IntPtr(e + OffPid)) != pid) continue;
                    d.OwnedHandles++;
                    if (Marshal.ReadInt32(new IntPtr(e + OffTypeIndex)) != fileTypeIndex) continue;
                    d.FileHandles++;

                    IntPtr raw = Marshal.ReadIntPtr(new IntPtr(e + OffHandle));
                    if (!DuplicateHandle(target, raw, self, out IntPtr dup, 0,
                                         false, DUPLICATE_SAME_ACCESS))
                    {
                        d.DupFailed++;
                        continue;
                    }
                    try
                    {
                        var sb = new StringBuilder(2048);
                        int n = GetFinalPathNameByHandleW(dup, sb, sb.Capacity, 0);
                        if (n <= 0) { d.PathFailed++; continue; }
                        string path = Normalize(sb.ToString());
                        d.SamplePaths.Add(path);
                        if (path.EndsWith(".clip", StringComparison.OrdinalIgnoreCase))
                            result.Add(path);
                    }
                    finally { CloseHandle(dup); }
                }
            }
            finally { CloseHandle(target); }
        }
        catch (Exception ex)
        {
            d.Note = "exception: " + ex.Message;
        }
        finally
        {
            if (snapshot != IntPtr.Zero) Marshal.FreeHGlobal(snapshot);
            if (probeHandle != IntPtr.Zero) CloseHandle(probeHandle);
            try { File.Delete(probePath); } catch { /* 忽略 */ }
        }

        d.Clips = result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return d.Clips;
    }

    public sealed class ScanDiagnostics
    {
        public long TotalHandles;
        public long OwnedHandles;
        public long FileHandles;
        public long DupFailed;
        public long PathFailed;
        public int FileTypeIndex = -1;
        public string? Note;
        public List<string> SamplePaths { get; } = new();
        public List<string> Clips { get; set; } = new();
    }

    private static IntPtr TakeSnapshot(out long handleCount)
    {
        handleCount = 0;
        int size = 1 << 20;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            IntPtr buf = Marshal.AllocHGlobal(size);
            int status = NtQuerySystemInformation(SystemExtendedHandleInformation, buf, size, out int needed);

            if (status == 0)
            {
                handleCount = Marshal.ReadIntPtr(buf).ToInt64();
                return buf;
            }

            Marshal.FreeHGlobal(buf);
            if (status != StatusInfoLengthMismatch) return IntPtr.Zero;
            size = Math.Max(size, needed + (1 << 20));
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 用「自己打开的一个已知文件句柄」反查 File 类型索引。
    /// 这样就不必对任意句柄调用 NtQueryObject —— 那个调用在命名管道等句柄上会永久挂住。
    /// </summary>
    private static int FindFileTypeIndex(IntPtr snapshot, long handleCount, IntPtr mine)
    {
        try
        {
            int me = Environment.ProcessId;
            long baseAddr = snapshot.ToInt64() + 16;
            for (long i = 0; i < handleCount; i++)
            {
                long e = baseAddr + i * HandleEntrySize;
                if (Marshal.ReadInt32(new IntPtr(e + OffPid)) != me) continue;
                if (Marshal.ReadIntPtr(new IntPtr(e + OffHandle)) != mine) continue;
                return Marshal.ReadInt32(new IntPtr(e + OffTypeIndex));
            }
            return -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>把 \\?\C:\... 归一化成 C:\...</summary>
    private static string Normalize(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];
        return path;
    }
}

