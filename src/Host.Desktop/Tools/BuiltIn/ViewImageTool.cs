using System.Diagnostics;
using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Tools.Safety;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// need09-05：view_image（第 7 号工具）。模型自主读取图片：
/// 空路径 Fail → 相对/绝对判定 → 扩展名+魔数头 → ≤8MB → 归一化（长边≤2048 q85 副本，sha16 去重）
/// → ToolResult.Success(摘要文本, Images=[ref])。base64 永不进事件/日志。
/// </summary>
public sealed class ViewImageTool : IToolHandler
{
    public const int SummaryMaxChars = 6000;

    public string Name => "view_image";

    public string Description =>
        "Read an image file (jpg/png/webp, max 8MB) from the workspace into the conversation. " +
        "Use path relative to workspace root or an absolute path. " +
        "Returns a short text summary plus the image reference for the model to see.";

    public ToolDefinition GetDefinition() => new(Name, Description,
        "{\"type\":\"object\",\"properties\":{"
        + "\"path\":{\"type\":\"string\",\"description\":\"Image path relative to workspace root, or an absolute file path. Required.\"},"
        + "\"detail\":{\"type\":\"string\",\"description\":\"Image detail level: auto|low|high. Defaults to auto.\"}"
        + "},\"required\":[\"path\"]}");

    public Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? pathArg = null;
            string detail = "auto";
            try
            {
                using JsonDocument doc = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("path", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.String)
                    pathArg = pEl.GetString();
                if (root.TryGetProperty("detail", out JsonElement dEl) && dEl.ValueKind == JsonValueKind.String)
                    detail = dEl.GetString() ?? "auto";
            }
            catch (JsonException ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"参数 JSON 解析失败：{ex.Message}。正确示例：{{\"path\": \"assets/a.png\", \"detail\": \"auto\"}}。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            if (string.IsNullOrWhiteSpace(pathArg))
            {
                return Task.FromResult(ToolResult.Fail(
                    "缺少必需参数 'path'。请传入工作区相对路径（如 assets/a.png）或绝对路径。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            string trimmed = pathArg.Trim();
            if (IsTraversalInput(trimmed))
            {
                return Task.FromResult(ToolResult.Fail(
                    $"拒绝路径遍历：'{trimmed}'。请使用工作区内相对路径或合法绝对路径（禁止 ../ 逃逸）。[PATH_TRAVERSAL]",
                    durationMs: sw.ElapsedMilliseconds));
            }

            if (IsIndexFile(trimmed))
            {
                return Task.FromResult(ToolResult.Fail(
                    $"拒绝直接读写索引文件：'{trimmed}'。请通过 skill_tool / memory_tool 管理 Skill/Memory。[PATH_TRAVERSAL]",
                    durationMs: sw.ElapsedMilliseconds));
            }

            string workspaceRoot = string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                ? Directory.GetCurrentDirectory()
                : context.WorkspaceRoot;
            string fullPath;
            try
            {
                fullPath = PathSafetyValidator.ResolveWorkspacePath(workspaceRoot, trimmed);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"路径格式非法，无法解析为有效路径：{ex.Message}",
                    durationMs: sw.ElapsedMilliseconds));
            }

            if (PathSafetyValidator.IsProtectedSystemPath(fullPath))
            {
                return Task.FromResult(ToolResult.Fail(
                    $"拒绝读取系统关键目录：'{fullPath}'。view_image 严禁触碰系统目录。[PATH_TRAVERSAL]",
                    durationMs: sw.ElapsedMilliseconds));
            }

            if (!ImageAttachmentStore.IsSupportedExtension(fullPath))
            {
                return Task.FromResult(ToolResult.Fail(
                    $"不支持的图片类型：'{fullPath}'。仅支持 jpg/png/webp（按扩展名 + 文件头双重校验，gif/bmp/tiff 请先转换）。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            if (!File.Exists(fullPath))
            {
                return Task.FromResult(ToolResult.Fail(
                    $"文件不存在：'{fullPath}'。请使用 list_dir 确认路径后重试。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            byte[] raw;
            try
            {
                var fi = new FileInfo(fullPath);
                if (fi.Length > ImageAttachmentStore.MaxImageBytes)
                {
                    return Task.FromResult(ToolResult.Fail(
                        $"图片超限：'{fullPath}'（{fi.Length / 1024} KB，上限 8MB）。请压缩后重试。",
                        durationMs: sw.ElapsedMilliseconds));
                }
                raw = File.ReadAllBytes(fullPath);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"文件读取失败：{ex.Message}。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            string mime = ImageAttachmentStore.MimeFromExtension(fullPath);
            if (!ImageAttachmentStore.CheckMagic(raw, mime))
            {
                return Task.FromResult(ToolResult.Fail(
                    $"文件头校验失败：'{fullPath}' 的内容不是有效的 {mime}（扩展名与魔数不符，可能已损坏或改名伪装）。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            var (bytes, normMime, w, h) = ImageAttachmentStore.NormalizeImage(raw, mime);
            string dataRoot = string.IsNullOrWhiteSpace(context.DataRoot)
                ? Path.GetTempPath() : context.DataRoot;
            var (imgRef, _) = ImageAttachmentStore.SaveImage(
                dataRoot, context.WorkspaceId ?? string.Empty, context.SessionId ?? string.Empty,
                Path.GetFileName(fullPath), normMime, bytes, w, h);

            detail = VisionDefaults.NormalizeDetail(detail);
            string summary = $"已读取 {imgRef.FileName}（{w}x{h}，{imgRef.SizeBytes / 1024} KB，sha16={imgRef.Sha16}，detail={detail}）。"
                + $"图片已作为附件加入本轮上下文，可直接描述其内容。";
            if (summary.Length > SummaryMaxChars)
                summary = summary.Substring(0, SummaryMaxChars);

            try
            {
                HaloLogger.Tool.Info($"view_image ok: path={fullPath} {w}x{h} sha16={imgRef.Sha16}");
            }
            catch { /* 日志失败不影响结果 */ }

            return Task.FromResult(ToolResult.Ok(summary,
                durationMs: sw.ElapsedMilliseconds,
                images: new List<ChatImageRef> { imgRef }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail(
                $"view_image 执行异常：{ex.Message}。请修正参数后重试。",
                durationMs: sw.ElapsedMilliseconds));
        }
        finally
        {
            sw.Stop();
        }
    }

    internal static bool IsTraversalInput(string input)
    {
        try
        {
            string norm = input.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            foreach (string seg in norm.Split(Path.DirectorySeparatorChar))
            {
                if (seg.Trim() == "..") return true;
            }
            return false;
        }
        catch { return true; }
    }

    internal static bool IsIndexFile(string input)
    {
        try
        {
            string name = Path.GetFileName(input.Trim()).ToLowerInvariant();
            return name is "_index.json" or "skill.json" or "memory.json";
        }
        catch { return false; }
    }
}
