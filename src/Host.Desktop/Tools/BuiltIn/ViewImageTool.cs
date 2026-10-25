using System.Diagnostics;
using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// need09-05：view_image（第 7 号工具）。模型自主读取图片：
/// 空路径 Fail → 相对/绝对判定 → 扩展名+魔数头 → ≤10MB → 无损直存（原字节落盘，sha16 去重，need09-fix）
/// → ToolResult.Success(摘要文本, Images=[ref])。base64 永不进事件/日志。
/// </summary>
[HaloAgent.Tools.Tool("view_image")]
public sealed class ViewImageTool : IToolHandler
{
    public const int SummaryMaxChars = 6000;

    public string Name => "view_image";

    // NEED17 PromptBrief: Read an image file into the conversation
    // S3 §4.1：追加错误码清单（英文，模型可读），模型可按 errorCode 分支重试/换图/放弃。
    public string Description =>
        "Read an image file (jpg/png/webp, max 10MB) from the workspace into the conversation. "
        + "Use path relative to workspace root or an absolute path. "
        + "Returns a short text summary plus the image reference for the model to see. "
        + "Workspace image only. Non-image files fail fast. On non-vision models the image is stripped and text continues (see notice). "
        + "Error codes: IMAGE_TOO_LARGE|NOT_AN_IMAGE|IMAGE_MISSING|PATH_TRAVERSAL.";

    /// <summary>S3 §3.1：view_image 四态机器可判错误码（成功时为 null）。</summary>
    public static class ErrorCodes
    {
        public const string ImageTooLarge = "IMAGE_TOO_LARGE";
        public const string NotAnImage = "NOT_AN_IMAGE";
        public const string ImageMissing = "IMAGE_MISSING";
        public const string PathTraversal = "PATH_TRAVERSAL";
    }

    public ToolDefinition GetDefinition()
    {
        string schema = new ToolSchemaBuilder()
            .AddString(
                "file_path",
                "Image path relative to workspace root, or an absolute file path. Required (alias 'path' accepted via compat shim).",
                required: true
            )
            .AddString("detail", "Image detail level: auto|low|high. Defaults to high.")
            .Build();
        return new(Name, Description, schema);
    }

    public Task<ToolResult> ExecuteAsync(
        ToolCallDto call,
        ToolContext context,
        CancellationToken ct = default
    )
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? pathArg = null;
            string detail = "high";
            try
            {
                // need22-P0 S3：NormalizedJson 单次解析（ToolArgs 统一入口）
                using var args = ToolArgs.Parse(call.ArgumentsJson);
                JsonElement root = args.Root;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    pathArg = args.GetString("file_path");
                    // NEED17 compat: alias 'path' accepted (02 shim搬运; handler双读不断老调用).
                    if (string.IsNullOrWhiteSpace(pathArg))
                    {
                        pathArg = args.GetString("path");
                    }
                    detail = args.GetString("detail") ?? "high";
                }
            }
            catch (JsonException ex)
            {
                return Task.FromResult(
                    ToolResult.Fail(
                        $"参数 JSON 解析失败：{ex.Message}。正确示例：{{\"file_path\": \"assets/a.png\", \"detail\": \"high\"}}。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (string.IsNullOrWhiteSpace(pathArg))
            {
                return Task.FromResult(
                    ToolResult.Fail(
                        "缺少必需参数 'file_path'（兼容别名 'path'）。请传入工作区相对路径（如 assets/a.png）或绝对路径。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            string trimmed = pathArg.Trim();
            if (IsTraversalInput(trimmed))
            {
                // S3 §4.1：文案不变，只把 [PATH_TRAVERSAL] 后缀改前缀码形态（Error 首 token 必为 CODE：）。
                return Task.FromResult(
                    ToolResult.FailCode(
                        ErrorCodes.PathTraversal,
                        $"拒绝路径遍历：'{trimmed}'。请使用工作区内相对路径或合法绝对路径（禁止 ../ 逃逸）。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (IsIndexFile(trimmed))
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        ErrorCodes.PathTraversal,
                        $"拒绝直接读写索引文件：'{trimmed}'。请通过 skill_tool / memory_tool 管理 Skill/Memory。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
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
                return Task.FromResult(
                    ToolResult.Fail(
                        $"路径格式非法，无法解析为有效路径：{ex.Message}",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (PathSafetyValidator.IsProtectedSystemPath(fullPath))
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        ErrorCodes.PathTraversal,
                        $"拒绝读取系统关键目录：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, fullPath)}'。view_image 严禁触碰系统目录。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (!ImageAttachmentStore.IsSupportedExtension(fullPath))
            {
                // S3 §4.1：非 jpg/png/webp 一律 NOT_AN_IMAGE（含 svg/gif/bmp 一刀切拒绝）。
                return Task.FromResult(
                    ToolResult.FailCode(
                        ErrorCodes.NotAnImage,
                        $"不支持的图片类型：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, fullPath)}'。仅支持 jpg/png/webp（按扩展名 + 文件头双重校验，gif/bmp/tiff 请先转换）（非 jpg/png/webp 一律此码，含 svg/gif）。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (!File.Exists(fullPath))
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        ErrorCodes.ImageMissing,
                        $"文件不存在：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, fullPath)}'。请使用 list_dir 确认路径后重试。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            byte[] raw;
            try
            {
                var fi = new FileInfo(fullPath);
                if (fi.Length > ImageAttachmentStore.MaxImageBytes)
                {
                    // S3 §4.1：超限走 IMAGE_TOO_LARGE，payload 含 {limit, actual} 供模型/前端判量级。
                    return Task.FromResult(
                        ToolResult.FailCode(
                            ErrorCodes.ImageTooLarge,
                            $"图片超限：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, fullPath)}'（{fi.Length / 1024} KB，上限 10MB）。请压缩后重试。",
                            payloadJson: BuildTooLargePayload(
                                ImageAttachmentStore.MaxImageBytes,
                                fi.Length
                            ),
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }
                // S2 有界读：快筛之后仍走 take(limit+1) 限长读，防 TOCTOU（先 stat 后读之间文件被替换撑大）。
                // S3：失败统一走 IMAGE_TOO_LARGE（含 io/empty），payload 记 limit 与 stat 到的 actual，不抛、不中断整轮。
                if (
                    !ImageAttachmentStore.TryReadBounded(
                        fullPath,
                        ImageAttachmentStore.MaxImageBytes,
                        out byte[]? bounded
                    )
                    || bounded == null
                )
                {
                    return Task.FromResult(
                        ToolResult.FailCode(
                            ErrorCodes.ImageTooLarge,
                            $"图片超限：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, fullPath)}'（{fi.Length / 1024} KB，上限 10MB）。请压缩后重试。",
                            payloadJson: BuildTooLargePayload(
                                ImageAttachmentStore.MaxImageBytes,
                                fi.Length
                            ),
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }
                raw = bounded;
            }
            catch (Exception ex)
            {
                return Task.FromResult(
                    ToolResult.Fail($"文件读取失败：{ex.Message}。", durationMs: sw.ElapsedMilliseconds)
                );
            }

            string mime = ImageAttachmentStore.MimeFromExtension(fullPath);
            if (!ImageAttachmentStore.CheckMagic(raw, mime))
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        ErrorCodes.NotAnImage,
                        $"文件头校验失败：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, fullPath)}' 的内容不是有效的 {mime}（扩展名与魔数不符，可能已损坏或改名伪装）。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            // need09-fix：无损入库：detail 仅作为发模清晰度提示（见摘要），落盘永远保留原字节；
            // 同图重读 sha 一致，直接命中去重缓存；历史读取 ResolveDataUrl 原样回传，永不降质。
            detail = VisionDefaults.NormalizeDetail(detail);
            var (bytes, normMime, w, h) = ImageAttachmentStore.PrepareLossless(raw, mime);
            string dataRoot = string.IsNullOrWhiteSpace(context.DataRoot)
                ? Path.GetTempPath()
                : context.DataRoot;
            var (imgRef, _) = ImageAttachmentStore.SaveImage(
                dataRoot,
                context.WorkspaceId ?? string.Empty,
                context.SessionId ?? string.Empty,
                Path.GetFileName(fullPath),
                normMime,
                bytes,
                w,
                h
            );

            string summary =
                $"已读取 {imgRef.FileName}（{w}x{h}，{imgRef.SizeBytes / 1024} KB，sha16={imgRef.Sha16}，detail={detail}）。"
                + $"图片已作为附件加入本轮上下文，可直接描述其内容。";
            if (summary.Length > SummaryMaxChars)
                summary = summary.Substring(0, SummaryMaxChars);

            try
            {
                HaloLogger.Tool.Info(
                    $"view_image ok: path={fullPath} {w}x{h} sha16={imgRef.Sha16}"
                );
            }
            catch
            { /* 日志失败不影响结果 */
            }

            return Task.FromResult(
                ToolResult.Ok(
                    summary,
                    durationMs: sw.ElapsedMilliseconds,
                    images: new List<ChatImageRef> { imgRef }
                )
            );
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                ToolResult.Fail(
                    $"view_image 执行异常：{ex.Message}。请修正参数后重试。",
                    durationMs: sw.ElapsedMilliseconds
                )
            );
        }
        finally
        {
            sw.Stop();
        }
    }

    /// <summary>S3 §4.1：IMAGE_TOO_LARGE 的结构化负载（{limit, actual}，actual 取 stat 值；stat 不可得时为 null）。</summary>
    private static string BuildTooLargePayload(long limitBytes, long? actualBytes)
    {
        return "{\"limit\":"
            + limitBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"actual\":"
            + (
                actualBytes.HasValue
                    ? actualBytes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "null"
            )
            + "}";
    }

    internal static bool IsTraversalInput(string input)
    {
        try
        {
            string norm = input.Replace(
                Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar
            );
            foreach (string seg in norm.Split(Path.DirectorySeparatorChar))
            {
                if (seg.Trim() == "..")
                    return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    internal static bool IsIndexFile(string input)
    {
        try
        {
            string name = Path.GetFileName(input.Trim()).ToLowerInvariant();
            return name is "_index.json" or "skill.json" or "memory.json";
        }
        catch
        {
            return false;
        }
    }
}
