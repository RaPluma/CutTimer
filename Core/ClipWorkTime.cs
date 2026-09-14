using System.Text;
using Microsoft.Data.Sqlite;

namespace CutTimer.Core;

/// <summary>一次 .clip 读取的结果。</summary>
public sealed record ClipReading(
    long WorkTimeMs,
    string? ProjectName,
    string? SettingType,
    string? Note)
{
    /// <summary>true 表示成功拿到作业时间。</summary>
    public bool Ok => Note is null && WorkTimeMs > 0;
}

/// <summary>
/// 从 CLIP STUDIO PAINT 的 .clip 文件里读出「作业时间」。
///
/// 原理（实测确认，见 clip-worktime/README.md）：
///   .clip 是 CELSYS 的 CSFCHUNK 分块容器，但内部嵌了一个完整的 SQLite 数据库。
///   找到 'SQLite format 3\0' 魔数后整段切出来，即可用标准 sqlite 读到
///   Canvas.CanvasWorkTime —— 单位毫秒，且 CSP 自己已排除了挂机/后台/操作其它画布的时间。
///
/// 注意：CSP 只在**保存**时把作业时间写回文件，编辑过程中不写盘。
///       所以本类只应在检测到文件 mtime 变化后调用。
/// </summary>
public static class ClipWorkTime
{
    private static readonly byte[] Magic = "SQLite format 3\0"u8.ToArray();
    private const int ChunkSize = 8 << 20;   // 8 MB

    /// <summary>
    /// 定位内嵌 SQLite 的起始偏移。
    /// 从文件末尾往回扫：数据库位于容器最后且一直延伸到 EOF，
    /// 倒着找通常只需读几 MB（实测 85 MB 的文件只需读约 4 MB）。
    /// </summary>
    public static long? FindSqliteOffset(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        long size = fs.Length;
        long end = size;
        byte[] head = Array.Empty<byte>();
        var buffer = new byte[ChunkSize];

        while (end > 0)
        {
            long start = Math.Max(0, end - ChunkSize);
            int want = (int)(end - start);
            fs.Seek(start, SeekOrigin.Begin);
            int read = fs.Read(buffer, 0, want);
            if (read <= 0) return null;

            // window = 本块 + 上一块遗留的重叠字节
            var window = new byte[read + head.Length];
            Buffer.BlockCopy(buffer, 0, window, 0, read);
            Buffer.BlockCopy(head, 0, window, read, head.Length);

            int hit = LastIndexOf(window, Magic);
            if (hit >= 0) return start + hit;

            // 保留末尾 overlap 字节，避免魔数跨块被漏掉
            int overlap = Math.Min(Magic.Length - 1, window.Length);
            head = window[^overlap..];
            end = start;
        }
        return null;
    }

    private static int LastIndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = haystack.Length - needle.Length; i >= 0; i--)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    /// <summary>读取一个 .clip 的作业时间。</summary>
    public static ClipReading Read(string path)
    {
        if (!File.Exists(path))
            return new ClipReading(0, null, null, "file-not-found");

        // 把内嵌 SQLite 切到临时文件（sqlite 无法从某个偏移直接打开）
        string tmp = Path.Combine(Path.GetTempPath(),
            $"cuttimer_{Environment.ProcessId}_{Guid.NewGuid():N}.db");
        try
        {
            long? offset = FindSqliteOffset(path);
            if (offset is null)
                return new ClipReading(0, null, null, "not-csf-container");

            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read,
                                            FileShare.ReadWrite | FileShare.Delete))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                src.Seek(offset.Value, SeekOrigin.Begin);
                src.CopyTo(dst);
            }

            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = tmp,
                Mode = SqliteOpenMode.ReadOnly,
            };
            using var conn = new SqliteConnection(csb.ToString());
            conn.Open();

            long workTime = 0;
            string? note = null;
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT CanvasWorkTime FROM Canvas LIMIT 1";
                object? v = cmd.ExecuteScalar();
                if (v is null || v is DBNull)
                    note = "no-worktime-value";
                else
                    workTime = Convert.ToInt64(v);
            }
            catch (SqliteException)
            {
                // CSP 2.1 之前制作的文件没有这一列（官方说明：不记录 2.1 之前的制作时间）
                note = "no-worktime-column";
            }

            return new ClipReading(workTime, ReadScalar(conn, "SELECT ProjectName FROM Project LIMIT 1"),
                                   ReadScalar(conn, "SELECT DefaultPageSettingType FROM Project LIMIT 1"), note);
        }
        catch (IOException)
        {
            // 文件还在写入（CSP 保存大文件时会先独占再写完）。
            // 标记为 busy，调用方应稍后重试，而不是当成永久失败。
            return new ClipReading(0, null, null, "busy");
        }
        catch (Exception ex)
        {
            return new ClipReading(0, null, null, "read-error: " + ex.Message);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private static string? ReadScalar(SqliteConnection conn, string sql)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            object? v = cmd.ExecuteScalar();
            return v is null || v is DBNull ? null : Convert.ToString(v);
        }
        catch (SqliteException)
        {
            return null;   // 老工程没有这一列
        }
    }
}
