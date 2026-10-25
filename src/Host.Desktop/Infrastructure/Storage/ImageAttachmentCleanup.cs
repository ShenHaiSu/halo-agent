using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Host.Desktop.Infrastructure.Logging;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>
/// S7-E3：附件 TTL / 清理（独立类，不改 `ImageAttachmentStore` 的入库主链路）。
/// 布局理解：全局 blob 区 `&lt;dataRoot&gt;/attachments` + 会话附件区
/// `&lt;dataRoot&gt;/sessions/&lt;ws&gt;/&lt;sess&gt;/attachments`。
/// </summary>
public static class ImageAttachmentCleanup
{
    // ===== S7-E3：附件 TTL / 清理 =====

    /// <summary>S7-E3：附件保留天数默认值（30 天，与参考 TTL 同量级）。</summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>S7-E3：一次清理的结果（dry-run 与真删共用，便于 UI/测试断言）。</summary>
    public sealed class AttachmentCleanupReport
    {
        /// <summary>扫描到的附件文件总数（含未超期）。</summary>
        public int ScannedFiles { get; set; }

        /// <summary>扫描到的附件总字节数（含未超期）。</summary>
        public long ScannedBytes { get; set; }

        /// <summary>超期文件数（dry-run 时 = 将要删除的数量）。</summary>
        public int ExpiredFiles { get; set; }

        /// <summary>超期文件字节数（dry-run 时 = 将要释放的字节）。</summary>
        public long ExpiredBytes { get; set; }

        /// <summary>实际删除成功的文件数（dry-run 恒为 0）。</summary>
        public int DeletedFiles { get; set; }

        /// <summary>是否 dry-run（true 表示只统计不删）。</summary>
        public bool DryRun { get; set; }

        /// <summary>生效的保留天数。</summary>
        public int RetentionDays { get; set; }
    }

    /// <summary>
    /// S7-E3 §4.3：附件清理入口。扫**全局 blob 区 + 全部会话附件区**，按 `LastWriteTimeUtc` 判定超期。
    ///
    /// 安全边界：
    /// ① 只删附件文件与清理后变空的目录，**永不碰** `messages.jsonl`（缺文件即 `IsMissing`，读取链路已兜）；
    /// ② `dryRun=true` 时只统计不删（验收 E3-1 的口径：先给出 total size / count 再让用户决定）；
    /// ③ 单个文件删除失败（占用/权限）只记数跳过，不中断整轮清理。
    ///
    /// `olderThan` 可为空：为 null 时用 `RetentionDays`（默认 30）。传 `TimeSpan.Zero` 即“全部视为超期”，供测试用。
    /// </summary>
    public static AttachmentCleanupReport CleanupAttachments(
        string dataRoot,
        bool dryRun = true,
        int retentionDays = DefaultRetentionDays,
        TimeSpan? olderThan = null,
        string? workspaceId = null,
        string? sessionId = null
    )
    {
        int days = retentionDays > 0 ? retentionDays : DefaultRetentionDays;
        TimeSpan age = olderThan ?? TimeSpan.FromDays(days);
        DateTime cutoffUtc = DateTime.UtcNow - age;
        var report = new AttachmentCleanupReport { DryRun = dryRun, RetentionDays = days, };
        string root = string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : dataRoot;

        // 收集待扫描根：全局 blob 区 + 会话附件区（限定 workspace/session 时只扫该会话，不碰全局区——
        // 全局 blob 是跨会话共享内容，单会话清理无权决定其去留，交由全局扫描处理）。
        var roots = new List<string>();
        bool scoped =
            !string.IsNullOrWhiteSpace(workspaceId) && !string.IsNullOrWhiteSpace(sessionId);
        string globalDir = ImageAttachmentStore.GetGlobalAttachmentsDir(root);
        if (!scoped && Directory.Exists(globalDir))
            roots.Add(globalDir);
        try
        {
            if (scoped)
            {
                string one = ImageAttachmentStore.GetAttachmentsDir(root, workspaceId!, sessionId!);
                if (Directory.Exists(one))
                    roots.Add(one);
            }
            else
            {
                string sessionsDir = Path.Combine(root, "sessions");
                if (Directory.Exists(sessionsDir))
                {
                    foreach (string ws in Directory.GetDirectories(sessionsDir))
                    {
                        foreach (string sess in Directory.GetDirectories(ws))
                        {
                            string att = Path.Combine(sess, "attachments");
                            if (Directory.Exists(att))
                                roots.Add(att);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                HaloLogger.App.Warn("[Image] TTL 扫描会话目录失败：" + ex.Message);
            }
            catch { }
        }

        foreach (string dir in roots)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                try
                {
                    HaloLogger.App.Warn("[Image] TTL 枚举失败：" + dir + " " + ex.Message);
                }
                catch { }
                continue;
            }
            foreach (string file in files)
            {
                FileInfo fi;
                try
                {
                    fi = new FileInfo(file);
                    if (!fi.Exists)
                        continue;
                }
                catch
                {
                    continue;
                }
                bool expired = fi.LastWriteTimeUtc < cutoffUtc;
                report.ScannedFiles++;
                report.ScannedBytes += fi.Length;
                if (!expired)
                    continue;
                report.ExpiredFiles++;
                report.ExpiredBytes += fi.Length;
                if (dryRun)
                    continue;
                try
                {
                    fi.Delete();
                    report.DeletedFiles++;
                }
                catch (Exception ex)
                {
                    try
                    {
                        HaloLogger.App.Warn("[Image] TTL 删除失败：" + fi.Name + " " + ex.Message);
                    }
                    catch { }
                }
            }
        }

        // 真删后顺手清理空目录（只清 attachments 下的日期目录，不动上层）。
        if (!dryRun)
        {
            foreach (string dir in roots)
                PruneEmptyDirs(dir);
        }

        try
        {
            HaloLogger.App.Info(
                $"[Image] TTL cleanup dryRun={dryRun} days={days} scanned={report.ScannedFiles} "
                    + $"({report.ScannedBytes}B) expired={report.ExpiredFiles} ({report.ExpiredBytes}B) "
                    + $"deleted={report.DeletedFiles}"
            );
        }
        catch { }
        return report;
    }

    private static void PruneEmptyDirs(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return;
            foreach (string d in Directory.GetDirectories(root))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(d).Any())
                        Directory.Delete(d);
                }
                catch { }
            }
        }
        catch { }
    }
}
