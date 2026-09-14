using Microsoft.Data.Sqlite;

namespace CutTimer.Core;

/// <summary>
/// 从 CLIP STUDIO PAINT 自己的设置里读出「上次打开/保存 .clip 用的目录」。
///
/// 为什么需要它：CSP 不持有打开文件的句柄、不暴露文档 API、窗口标题里也没有文件名，
/// 所以外部无法直接知道你在画哪张。但 CSP 会把**打开/保存对话框最后使用的目录**
/// 记在 WorkFolder.sqlite 的 CanvasDocument 键里 —— 拿它就等于知道了你当前在哪工作，
/// 于是可以自动把那个目录加入监视，不用每次手动添加。
///
/// 位置：%APPDATA%\CELSYS\CLIPStudioPaint\&lt;版本&gt;\Placement\WorkFolder.sqlite
/// </summary>
public static class CspWorkspace
{
    /// <summary>取 CSP 记录的上次 .clip 目录；取不到返回 null。</summary>
    public static string? ReadLastDocumentFolder()
    {
        try
        {
            string? db = FindWorkFolderDb();
            if (db is null) return null;

            // 先拷一份再读：CSP 随时可能写这个库，直接开会撞锁
            string tmp = Path.Combine(Path.GetTempPath(), $"cutimer_wf_{Guid.NewGuid():N}.sqlite");
            try
            {
                File.Copy(db, tmp, overwrite: true);

                var csb = new SqliteConnectionStringBuilder
                {
                    DataSource = tmp,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                };
                using var conn = new SqliteConnection(csb.ToString());
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Path FROM FolderPath WHERE Key = 'CanvasDocument' LIMIT 1";
                object? v = cmd.ExecuteScalar();
                string? path = v as string;

                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    return path;
                return null;
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* 忽略 */ }
            }
        }
        catch
        {
            // 读不到不是错误：CSP 没装、没打开过文件、库被锁……都走这里
            return null;
        }
    }

    /// <summary>找版本号最大的那个 WorkFolder.sqlite。</summary>
    private static string? FindWorkFolderDb()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CELSYS", "CLIPStudioPaint");
        if (!Directory.Exists(root)) return null;

        string? best = null;
        Version bestVer = new(0, 0);

        foreach (string verDir in Directory.GetDirectories(root))
        {
            string db = Path.Combine(verDir, "Placement", "WorkFolder.sqlite");
            if (!File.Exists(db)) continue;

            // 目录名形如 1.5.0，取最大的那个（装了多版本时用新的）
            if (!Version.TryParse(Path.GetFileName(verDir), out Version? v)) v = new Version(0, 0);
            if (v > bestVer) { bestVer = v; best = db; }
        }
        return best;
    }
}
