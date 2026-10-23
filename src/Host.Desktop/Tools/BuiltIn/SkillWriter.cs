using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>need21-B04: Skill/Memory 写入/调度（原 SkillToolShared 写路径；事件经 ChangedNotifier 中立化）。</summary>
public static class SkillWriter
{
    private static readonly object _writeLock = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        (DateTime At, object? Value)
    > _snapshot = new(StringComparer.Ordinal);

    public static void InvalidateSnapshots() => _snapshot.Clear();

    public static Action<bool, string, string>? ChangedNotifier { get; set; }

    private static void PublishChanged(bool isSkill, string scope, string name)
    {
        try
        {
            ChangedNotifier?.Invoke(isSkill, scope, name);
        }
        catch { }
    }

    public sealed record WriteResult(
        bool Ok,
        string Message,
        SkillFileReader.SkillMemoryEntry? Entry,
        int DeltaTokens
    );

    public static WriteResult CreateOrUpdate(
        bool isSkill,
        string scope,
        string name,
        string? content,
        string? pin,
        int? priority,
        bool? enabled,
        ToolContext context,
        string dataRoot,
        bool isCreate
    )
    {
        string kind = SkillFileReader.KindLabel(isSkill);
        string? nameErr = SkillValidator.ValidateName(name);
        if (nameErr != null)
            return new WriteResult(false, nameErr, null, 0);
        string n = SkillValidator.NormalizeName(name);
        if (scope != "global" && scope != "project")
            return new WriteResult(
                false,
                $"scope 非法：'{scope}'（仅允许 global|project；list 可用 all）。[SKILL_VALIDATION]",
                null,
                0
            );
        if (scope == "project" && !SkillFileReader.HasWorkspace(context))
            return new WriteResult(
                false,
                "当前无工作区（后台任务），project 域不可用；请用 scope=global 或打开工作区后重试。[SKILL_VALIDATION]",
                null,
                0
            );
        if (content != null && content.Length > SkillValidator.MaxBodyChars)
            return new WriteResult(
                false,
                $"正文超限：{content.Length} 字符（上限 {SkillValidator.MaxBodyChars}，约 {SkillValidator.EstTokens(content)} tokens）。请截断到 {SkillValidator.MaxBodyChars} 字符内后重试。[SKILL_VALIDATION]",
                null,
                0
            );
        string? normPin = pin == null ? null : SkillValidator.NormalizePin(isSkill, pin);
        if (pin != null && normPin == null)
            return new WriteResult(false, SkillValidator.PinError(isSkill), null, 0);
        if (priority.HasValue && (priority < 0 || priority > 1000))
            return new WriteResult(
                false,
                "priority 非法：允许 0..1000，默认 100。[SKILL_VALIDATION]",
                null,
                0
            );
        if (isCreate && string.IsNullOrWhiteSpace(content))
            return new WriteResult(
                false,
                $"create 需要 content（readme 正文，≤{SkillValidator.MaxBodyChars} 字符）。[SKILL_VALIDATION]",
                null,
                0
            );
        if (!isCreate && content == null && normPin == null && priority == null && enabled == null)
            return new WriteResult(
                false,
                "update 至少需要 content/pin/priority/enabled 其中之一。[SKILL_VALIDATION]",
                null,
                0
            );

        lock (_writeLock)
        {
            string baseDir =
                scope == "global"
                    ? SkillFileReader.GlobalBase(dataRoot, isSkill)
                    : SkillFileReader.ProjectBase(context.WorkspaceRoot, isSkill);
            Directory.CreateDirectory(baseDir);
            var existing = SkillFileReader.ReadOne(isSkill, scope, Path.Combine(baseDir, n));
            if (!isCreate && existing == null)
                return new WriteResult(
                    false,
                    $"不存在的 {kind} '{n}'（scope={scope}）：拒绝自动转 create（防拼写污染），请先 action=list 确认名称。[SKILL_VALIDATION]",
                    null,
                    0
                );

            string body = content ?? existing?.BodyMarkdown ?? string.Empty;
            string finalPin = normPin ?? existing?.Pin ?? SkillValidator.DefaultPin(isSkill);
            int finalPriority = priority ?? existing?.Priority ?? 100;
            bool finalEnabled = enabled ?? existing?.Enabled ?? true;
            int finalVersion = (existing?.Version ?? 0) + 1;

            var domain = SkillFileReader.ReadDomain(isSkill, scope, baseDir);
            int used = domain.Where(e => e.Name != n).Sum(e => e.BodyMarkdown.Length);
            int others = domain.Count(e => e.Name != n);
            if (used + body.Length > SkillValidator.MaxDomainChars)
            {
                var biggest = domain.OrderByDescending(e => e.BodyMarkdown.Length).FirstOrDefault();
                string hint =
                    biggest != null
                        ? $"建议删除 '{biggest.Name}'（约 {biggest.BodyMarkdown.Length} 字符，~{biggest.EstTokens} tokens）后再试。"
                        : "请删除部分条目后再试。";
                return new WriteResult(
                    false,
                    $"配额超限：{scope}-{SkillFileReader.KindDir(isSkill)} 已用 {used}/{SkillValidator.MaxDomainChars} 字符，本次 +{body.Length} 将超限。"
                        + hint
                        + "[SKILL_VALIDATION]",
                    null,
                    0
                );
            }
            if (existing == null && others >= SkillValidator.MaxCountPerDomain)
                return new WriteResult(
                    false,
                    $"数量超限：{scope}-{SkillFileReader.KindDir(isSkill)} 已有 {others}/{SkillValidator.MaxCountPerDomain} 条，请先删除再新建。[SKILL_VALIDATION]",
                    null,
                    0
                );

            string dir = Path.Combine(baseDir, n);
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "assets"));
            string readme = "# " + n + "\n\n" + body.Trim() + "\n";
            string front =
                "---\nversion: "
                + finalVersion
                + "\nenabled: "
                + (finalEnabled ? "true" : "false")
                + "\npriority: "
                + finalPriority
                + "\npin: "
                + finalPin
                + "\nscope_hint: \"\"\nupdated_at: \""
                + DateTime.UtcNow.ToString("o")
                + "\"\n---\n\n";
            WriteAtomic(Path.Combine(dir, "readme.md"), front + readme);
            WriteAtomic(
                Path.Combine(dir, isSkill ? "skill.json" : "memory.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        name = n,
                        version = finalVersion,
                        enabled = finalEnabled,
                        priority = finalPriority,
                        pin = finalPin,
                        updatedAtUtc = DateTime.UtcNow.ToString("o"),
                    },
                    new JsonSerializerOptions { WriteIndented = true }
                )
            );

            var entry = SkillFileReader.ReadOne(isSkill, scope, dir);
            SkillFileReader.RebuildIndex(
                baseDir,
                SkillFileReader.ReadDomain(isSkill, scope, baseDir),
                isSkill
            );
            InvalidateSnapshots();
            PublishChanged(isSkill, scope, n);

            int delta = (entry?.EstTokens ?? 0) - (existing?.EstTokens ?? 0);
            string deltaText = (delta >= 0 ? "+" : "") + delta + " tokens";
            string action = existing == null ? "已创建" : "已更新（覆盖保存）";
            string msg = $"{action} {kind} '{n}'（scope={scope}）。下轮生效；常驻索引 {deltaText}。";
            if (isSkill && finalPin == "full" && entry != null)
                msg += $"警告：pin=full 使正文每轮常驻（约 +{entry.EstTokens} tokens/轮），确认常驻请保留，否则改回 auto。";
            // need22-P0 S5：正文疑似密钥落盘审计（不阻断写入、不篡改正文，仅告警）
            try
            {
                if (!string.IsNullOrEmpty(body) && SecretScrubber.ContainsSecret(body))
                {
                    msg += "（注：正文疑似含密钥明文，已记录审计告警；请勿长期存放明文密钥。）";
                    HaloLogger.App.Warn($"[Skill] {kind} '{n}'（scope={scope}）正文疑似含密钥，已落盘并审计告警。");
                }
            }
            catch
            { /* 审计失败不阻断写入 */
            }
            return new WriteResult(true, msg, entry, delta);
        }
    }

    public static WriteResult Delete(
        bool isSkill,
        string scope,
        string name,
        ToolContext context,
        string dataRoot
    )
    {
        string kind = SkillFileReader.KindLabel(isSkill);
        string? nameErr = SkillValidator.ValidateName(name);
        if (nameErr != null)
            return new WriteResult(false, nameErr, null, 0);
        string n = SkillValidator.NormalizeName(name);
        if (scope != "global" && scope != "project")
            return new WriteResult(
                false,
                $"scope 非法：'{scope}'（仅允许 global|project）。[SKILL_VALIDATION]",
                null,
                0
            );
        if (scope == "project" && !SkillFileReader.HasWorkspace(context))
            return new WriteResult(false, "当前无工作区（后台任务），project 域不可用。[SKILL_VALIDATION]", null, 0);
        lock (_writeLock)
        {
            string baseDir =
                scope == "global"
                    ? SkillFileReader.GlobalBase(dataRoot, isSkill)
                    : SkillFileReader.ProjectBase(context.WorkspaceRoot, isSkill);
            string dir = Path.Combine(baseDir, n);
            var existing = SkillFileReader.ReadOne(isSkill, scope, dir);
            if (existing == null)
                return new WriteResult(
                    false,
                    $"不存在的 {kind} '{n}'（scope={scope}），无需删除；请先 action=list 确认。[SKILL_VALIDATION]",
                    null,
                    0
                );
            string bak = Path.Combine(
                baseDir,
                n + ".deleted-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bak"
            );
            try
            {
                if (Directory.Exists(bak))
                    Directory.Delete(bak, recursive: true);
                CopyDirectory(dir, bak);
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                return new WriteResult(false, $"删除失败：{ex.Message}。[SKILL_VALIDATION]", null, 0);
            }
            SkillFileReader.RebuildIndex(
                baseDir,
                SkillFileReader.ReadDomain(isSkill, scope, baseDir),
                isSkill
            );
            InvalidateSnapshots();
            PublishChanged(isSkill, scope, n);
            try
            {
                HaloLogger.App.Warn($"[Skill] 已删除{scope} {kind} '{n}'（备份 {bak}）");
            }
            catch { }
            return new WriteResult(
                true,
                $"已删除{scope} {kind} '{n}'，不可撤销（仅留一份备份 {Path.GetFileName(bak)}）。下轮生效；常驻索引 -{existing.EstTokens} tokens。",
                null,
                -existing.EstTokens
            );
        }
    }

    public static WriteResult SetEnabled(
        bool isSkill,
        string scope,
        string name,
        bool enable,
        ToolContext context,
        string dataRoot
    )
    {
        string kind = SkillFileReader.KindLabel(isSkill);
        string? nameErr = SkillValidator.ValidateName(name);
        if (nameErr != null)
            return new WriteResult(false, nameErr, null, 0);
        string n = SkillValidator.NormalizeName(name);
        if (scope != "global" && scope != "project")
            return new WriteResult(
                false,
                $"scope 非法：'{scope}'（仅允许 global|project）。[SKILL_VALIDATION]",
                null,
                0
            );
        if (scope == "project" && !SkillFileReader.HasWorkspace(context))
            return new WriteResult(false, "当前无工作区（后台任务），project 域不可用。[SKILL_VALIDATION]", null, 0);
        lock (_writeLock)
        {
            string baseDir =
                scope == "global"
                    ? SkillFileReader.GlobalBase(dataRoot, isSkill)
                    : SkillFileReader.ProjectBase(context.WorkspaceRoot, isSkill);
            var existing = SkillFileReader.ReadOne(isSkill, scope, Path.Combine(baseDir, n));
            if (existing == null)
                return new WriteResult(
                    false,
                    $"不存在的 {kind} '{n}'（scope={scope}）；请先 action=list 确认。[SKILL_VALIDATION]",
                    null,
                    0
                );
            return
                CreateOrUpdate(
                    isSkill,
                    scope,
                    n,
                    content: null,
                    pin: null,
                    priority: null,
                    enabled: enable,
                    context,
                    dataRoot,
                    isCreate: false
                )
                    is WriteResult r
                && r.Ok
                ? new WriteResult(
                    true,
                    $"已{(enable ? "启用" : "停用")} {kind} '{n}'（scope={scope}）。下轮生效。",
                    r.Entry,
                    0
                )
                : new WriteResult(false, $"开关切换失败：{kind} '{n}'。[SKILL_VALIDATION]", null, 0);
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, content, Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (string d in Directory.GetDirectories(src))
            CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    // ---------------- 工具入口 ----------------
}
