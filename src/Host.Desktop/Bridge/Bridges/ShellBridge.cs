using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using CefSharp;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;

namespace Host.Desktop.Bridge;

/// <summary>need21-B02: 资源管理器/外部链接/日志/标题。</summary>
public sealed partial class HostBridgeFacade
{
    public Task<BridgeResponse<bool>> OpenPathInExplorer(string targetPath)
    {
        try
        {
            var pathToOpen = string.IsNullOrWhiteSpace(targetPath)
                ? _storageManager.CurrentDataRoot
                : Path.GetFullPath(targetPath);

            if (File.Exists(pathToOpen))
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{pathToOpen}\"",
                        UseShellExecute = true
                    }
                );
                return Task.FromResult(BridgeResponse<bool>.Success(true));
            }

            if (!Directory.Exists(pathToOpen))
            {
                Directory.CreateDirectory(pathToOpen);
            }

            Process.Start(
                new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{pathToOpen}\"",
                    UseShellExecute = true
                }
            );

            return Task.FromResult(BridgeResponse<bool>.Success(true));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"OpenPathInExplorer failed for '{targetPath}'", ex);
            return Task.FromResult(BridgeResponse<bool>.Fail("OPEN_EXPLORER_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// 聊天 markdown 链接统一出口：http(s) 交系统默认浏览器，本地路径交 explorer 定位，永不在 CEF 主帧内导航。
    /// 安全铁律：文件一律 explorer /select 定位，绝不 UseShellExecute 直接打开（防模型输出的 exe/bat 被执行）。
    /// </summary>
    public Task<BridgeResponse<bool>> OpenExternalLink(string href, string? workspaceRoot = null)
    {
        try
        {
            string link = (href ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(link))
            {
                return Task.FromResult(BridgeResponse<bool>.Fail("EMPTY_LINK", "链接为空"));
            }
            // 防护：拒绝 javascript:/data:/vbscript: 等可执行伪协议
            if (
                link.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || link.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || link.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)
            )
            {
                return Task.FromResult(BridgeResponse<bool>.Fail("BLOCKED_SCHEME", "该链接协议不允许打开"));
            }
            if (
                link.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            )
            {
                Process.Start(new ProcessStartInfo { FileName = link, UseShellExecute = true });
                return Task.FromResult(BridgeResponse<bool>.Success(true, "已交系统浏览器打开"));
            }
            // 本地路径：支持 file:/// 与绝对/工作区相对路径，统一落到 explorer 定位
            string candidate = link;
            if (candidate.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    candidate = new Uri(candidate).LocalPath;
                }
                catch
                {
                    return Task.FromResult(BridgeResponse<bool>.Fail("BAD_FILE_URL", "文件链接格式非法"));
                }
            }
            string fullPath;
            try
            {
                fullPath = Host.Desktop.Tools.Safety.PathSafetyValidator.ResolveWorkspacePath(
                    workspaceRoot ?? string.Empty,
                    candidate
                );
            }
            catch (Exception ex)
            {
                return Task.FromResult(
                    BridgeResponse<bool>.Fail("BAD_PATH", "路径无法解析：" + ex.Message)
                );
            }
            if (File.Exists(fullPath))
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{fullPath}\"",
                        UseShellExecute = true
                    }
                );
                return Task.FromResult(BridgeResponse<bool>.Success(true, "已在资源管理器中定位文件"));
            }
            if (Directory.Exists(fullPath))
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{fullPath}\"",
                        UseShellExecute = true
                    }
                );
                return Task.FromResult(BridgeResponse<bool>.Success(true, "已在资源管理器中打开目录"));
            }
            return Task.FromResult(
                BridgeResponse<bool>.Fail("PATH_NOT_FOUND", $"本地路径不存在：'{fullPath}'")
            );
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"OpenExternalLink failed for '{href}'", ex);
            return Task.FromResult(BridgeResponse<bool>.Fail("OPEN_LINK_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// S7-E3 §4.3-2：附件 TTL 清理入口（先做“统计口径 + 手动触发”，自动清待观察一轮后再开）。
    /// `dryRun=true` 只统计不删（默认，避免误删）；`retentionDays` 默认 30 天。
    /// 只清附件文件与空目录，**不碰** `messages.jsonl`（缺文件即 IsMissing，读取链路已兜）。
    /// </summary>
    public Task<BridgeResponse<string>> CleanupImageAttachments(
        int retentionDays = 30,
        bool dryRun = true
    )
    {
        try
        {
            int days = Math.Clamp(retentionDays, 1, 3650);
            var report = ImageAttachmentCleanup.CleanupAttachments(
                _storageManager.CurrentDataRoot,
                dryRun: dryRun,
                retentionDays: days
            );
            string payload = JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
            );
            string msg = dryRun
                ? $"统计完成：共 {report.ScannedFiles} 个附件（{report.ScannedBytes} 字节），其中 {report.ExpiredFiles} 个超期（{report.ExpiredBytes} 字节，超过 {days} 天），未删除。"
                : $"清理完成：删除 {report.DeletedFiles} 个超期附件，释放 {report.ExpiredBytes} 字节。";
            HaloLogger.App.Info(
                $"[HostBridge] CleanupImageAttachments dryRun={dryRun} days={days}"
            );
            return Task.FromResult(BridgeResponse<string>.Success(payload, msg));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("CleanupImageAttachments failed", ex);
            return Task.FromResult(
                BridgeResponse<string>.Fail("ATTACHMENT_CLEANUP_ERROR", ex.Message)
            );
        }
    }

    public async Task<BridgeResponse<int>> CleanupExpiredLogs(int retentionDays = 14)
    {
        try
        {
            var effectiveDays = Math.Clamp(retentionDays, 1, 365);
            var settings = _storageManager.LoadAppSettings();
            settings.LogRetentionDays = effectiveDays;
            await _storageManager.SaveAppSettingsAsync(settings).ConfigureAwait(false);

            var deleted = HaloLogger.CleanupExpiredLogs(effectiveDays);
            HaloLogger.App.Info(
                $"Manual log cleanup executed (retentionDays={effectiveDays}, deleted={deleted})."
            );
            await HaloLogger.FlushAsync().ConfigureAwait(false);
            return BridgeResponse<int>.Success(deleted, $"清理完成，共移除 {deleted} 个过期日志文件。");
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("CleanupExpiredLogs failed", ex);
            return BridgeResponse<int>.Fail("LOG_CLEANUP_ERROR", ex.Message);
        }
    }

    public async Task<BridgeResponse<bool>> WriteDiagnosticLog(string channel, string message)
    {
        try
        {
            var normalized = (channel ?? "app").Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "llm":
                    HaloLogger.Llm.Info(message);
                    break;
                case "tool":
                    HaloLogger.Tool.Info(message);
                    break;
                default:
                    HaloLogger.App.Info(message);
                    break;
            }

            await HaloLogger.FlushAsync().ConfigureAwait(false);
            return BridgeResponse<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return BridgeResponse<bool>.Fail("LOG_WRITE_ERROR", ex.Message);
        }
    }

    public async Task<BridgeResponse<bool>> SetWindowTitle(string title)
    {
        try
        {
            await InvokeOnUiThreadAsync(() =>
                {
                    if (_mainForm != null && !_mainForm.IsDisposed)
                    {
                        _mainForm.Text = string.IsNullOrWhiteSpace(title)
                            ? "Halo-Agent Desktop"
                            : title;
                    }
                    return true;
                })
                .ConfigureAwait(false);

            return BridgeResponse<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return BridgeResponse<bool>.Fail("WINDOW_TITLE_ERROR", ex.Message);
        }
    }

    // =========================================================================
    // Stage 02: 全局网络代理中枢 (ProxyHub) 与多服务商配置引擎 IPC 接口
    // =========================================================================


    /// <summary>need10-02：打开文件夹。查到 DirPath 后复用 OpenPathInExplorer 打开目录；查不到 → SKILL_NOT_FOUND。</summary>
    public async Task<BridgeResponse<bool>> OpenSkillMemoryFolder(
        string kind,
        string scope,
        string name,
        string? workspaceId
    )
    {
        try
        {
            var k = ParseSkillKind(kind);
            string s = (scope ?? string.Empty).Trim().ToLowerInvariant();
            if (s != "global" && s != "project")
                return BridgeResponse<bool>.Fail(
                    "SKILL_VALIDATION",
                    $"打开文件夹 scope 须为 global|project（收到 '{scope}'）。"
                );
            string? wsRoot = await ResolveWorkspaceRootAsync(workspaceId).ConfigureAwait(false);
            var sc = s == "global" ? SkillMemoryScope.Global : SkillMemoryScope.Project;
            SkillMemoryEntry? entry;
            try
            {
                entry = SkillMemoryServiceInternal.Get(sc, k, name ?? string.Empty, wsRoot);
            }
            catch (SkillMemoryValidationException vex)
            {
                return BridgeResponse<bool>.Fail(vex.Code, vex.Message);
            }
            if (entry == null || !Directory.Exists(entry.DirPath))
                return BridgeResponse<bool>.Fail(
                    "SKILL_NOT_FOUND",
                    $"条目不存在：{(name ?? string.Empty).Trim()}。"
                );
            var res = await OpenPathInExplorer(entry.DirPath).ConfigureAwait(false);
            if (!res.Ok)
                return BridgeResponse<bool>.Fail(
                    res.Code ?? "OPEN_EXPLORER_ERROR",
                    res.Message ?? "打开文件夹失败"
                );
            return BridgeResponse<bool>.Success(true);
        }
        catch (SkillMemoryValidationException vex)
        {
            return BridgeResponse<bool>.Fail(vex.Code, vex.Message);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("OpenSkillMemoryFolder failed", ex);
            return BridgeResponse<bool>.Fail("SKILL_BRIDGE_ERROR", ex.Message);
        }
    }
}
