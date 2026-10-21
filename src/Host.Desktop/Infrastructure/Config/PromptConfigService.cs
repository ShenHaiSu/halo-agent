using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Infrastructure.Config;

/// <summary>
/// need06-02：全局前置提示词配置服务（config/prompt.json + 原子写 + 启动初始化）。
/// 对标 ProviderConfigService 范式；本配置无密钥，无需 DPAPI。
/// </summary>
public sealed class PromptConfigService
{
    private readonly StoragePathManager _storagePathManager;
    private readonly object _syncLock = new();

    public string PromptConfigFilePath =>
        Path.Combine(_storagePathManager.ConfigDirectory, "prompt.json");

    public PromptConfigService(StoragePathManager storagePathManager)
    {
        _storagePathManager =
            storagePathManager ?? throw new ArgumentNullException(nameof(storagePathManager));
        EnsureInitialized();
    }

    public void EnsureInitialized()
    {
        lock (_syncLock)
        {
            Directory.CreateDirectory(_storagePathManager.ConfigDirectory);
            if (!File.Exists(PromptConfigFilePath))
            {
                var def = PromptDefaults.CreateDefault();
                AtomicFileWriter.WriteJsonAtomic(PromptConfigFilePath, def);
            }
        }
    }

    public PromptConfigDocument GetPromptConfigInternal() => LoadWithMeta().Document;

    public (PromptConfigDocument Document, bool IsDefault, string? FallbackReason) LoadWithMeta() =>
        LoadInternal();

    public Infrastructure.Storage.StoragePathManager StorageManager => _storagePathManager;

    public PromptConfigDocument SavePromptConfig(PromptConfigDocument incoming)
    {
        if (incoming == null)
            throw new ArgumentNullException(nameof(incoming));
        lock (_syncLock)
        {
            var normalized = NormalizeAndValidate(incoming, forSave: true);
            normalized.UpdatedAtUtc = DateTime.UtcNow;
            if (File.Exists(PromptConfigFilePath))
            {
                try
                {
                    string bakPath = PromptConfigFilePath + ".bak";
                    File.Copy(PromptConfigFilePath, bakPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    HaloLogger.App.Warn("[Prompt] prompt.json.bak 备份失败（继续保存）", ex);
                }
            }
            AtomicFileWriter.WriteJsonAtomic(PromptConfigFilePath, normalized);
            HaloLogger.App.Info("[Prompt] prompt.json 已保存（原子写盘）。");
            return Clone(normalized);
        }
    }

    public PromptConfigDocument ResetPromptConfig(string? blockId)
    {
        lock (_syncLock)
        {
            var def = PromptDefaults.CreateDefault();
            if (string.IsNullOrWhiteSpace(blockId))
            {
                return SavePromptConfig(def);
            }
            string id = blockId.Trim();
            var (current, _, _) = LoadInternal();
            switch (id.ToLowerInvariant())
            {
                case "identity":
                    current.IdentityText = PromptDefaults.IdentityText;
                    current.EnableIdentity = true;
                    break;
                case "runtime":
                    current.RuntimeTemplate = PromptDefaults.RuntimeTemplate;
                    current.EnableRuntime = true;
                    break;
                case "cmdpython":
                case "cmd-python":
                case "cmd_python":
                    current.CmdPythonText = PromptDefaults.CmdPythonText;
                    current.EnableCmdPython = true;
                    break;
                case "toolguide":
                case "tool-guide":
                case "tool_guide":
                    current.ToolGuideTemplate = PromptDefaults.ToolGuideTemplate;
                    current.EnableToolGuide = true;
                    break;
                case "ephemeral":
                    current.EphemeralTemplate = PromptDefaults.EphemeralTemplate;
                    current.EnableEphemeral = true;
                    break;
                case "custom":
                    current.CustomText = string.Empty;
                    current.EnableCustom = false;
                    current.CustomPlacement = "tail";
                    break;
                default:
                    throw new PromptConfigValidationException(id, $"未知提示词块 ID：{id}");
            }
            return SavePromptConfig(current);
        }
    }

    private (PromptConfigDocument Document, bool IsDefault, string? FallbackReason) LoadInternal()
    {
        string path = PromptConfigFilePath;
        if (!File.Exists(path))
        {
            var def = PromptDefaults.CreateDefault();
            try
            {
                AtomicFileWriter.WriteJsonAtomic(path, def);
            }
            catch
            { /* ignore */
            }
            return (Clone(def), true, null);
        }
        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[Prompt] prompt.json 读取失败，已回退默认", ex);
            return (PromptDefaults.CreateDefault(), true, "prompt.json 读取失败，已回退默认");
        }
        if (string.IsNullOrWhiteSpace(raw))
        {
            HaloLogger.App.Warn("[Prompt] prompt.json 为空，已回退默认");
            return (PromptDefaults.CreateDefault(), true, "prompt.json 为空，已回退默认");
        }
        PromptConfigDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<PromptConfigDocument>(
                raw,
                HaloJsonOptions.IndentedCamelCase
            );
        }
        catch (Exception ex)
        {
            string corruptPath = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            try
            {
                File.Copy(path, corruptPath, overwrite: false);
            }
            catch
            { /* ignore */
            }
            HaloLogger.App.Warn("[Prompt] prompt.json 损坏，已回退默认（原文件保留为 " + corruptPath + "）", ex);
            return (
                PromptDefaults.CreateDefault(),
                true,
                "prompt.json 损坏，已回退默认（原文件保留为 prompt.json.corrupt-<timestamp>）"
            );
        }
        if (doc == null)
        {
            return (PromptDefaults.CreateDefault(), true, "prompt.json 解析为空，已回退默认");
        }
        try
        {
            var normalized = NormalizeAndValidate(doc, forSave: false);
            return (normalized, false, null);
        }
        catch (PromptConfigValidationException vex)
        {
            HaloLogger.App.Warn($"[Prompt] prompt.json 超限（块 {vex.BlockId}），已回退默认: {vex.Message}");
            var def = PromptDefaults.CreateDefault();
            return (def, true, $"prompt.json 配置超限（块 {vex.BlockId}），已回退默认");
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[Prompt] prompt.json 校验失败，已回退默认", ex);
            return (PromptDefaults.CreateDefault(), true, "prompt.json 校验失败，已回退默认");
        }
    }

    private static PromptConfigDocument Clone(PromptConfigDocument src)
    {
        string json = JsonSerializer.Serialize(src, HaloJsonOptions.IndentedCamelCase);
        return JsonSerializer.Deserialize<PromptConfigDocument>(
                json,
                HaloJsonOptions.IndentedCamelCase
            ) ?? PromptDefaults.CreateDefault();
    }

    private static PromptConfigDocument NormalizeAndValidate(
        PromptConfigDocument incoming,
        bool forSave
    )
    {
        var doc = new PromptConfigDocument
        {
            Version = 1,
            EnableHaloPrefix = incoming.EnableHaloPrefix,
            EnableIdentity = incoming.EnableIdentity,
            EnableRuntime = incoming.EnableRuntime,
            EnableCmdPython = incoming.EnableCmdPython,
            EnableToolGuide = incoming.EnableToolGuide,
            EnableEphemeral = incoming.EnableEphemeral,
            EnableCustom = incoming.EnableCustom,
            CustomPlacement = string.Equals(
                incoming.CustomPlacement,
                "head",
                StringComparison.OrdinalIgnoreCase
            )
                ? "head"
                : "tail",
            CustomText = incoming.CustomText ?? string.Empty,
            IdentityText = incoming.IdentityText ?? string.Empty,
            RuntimeTemplate = incoming.RuntimeTemplate ?? string.Empty,
            CmdPythonText = incoming.CmdPythonText ?? string.Empty,
            ToolGuideTemplate = incoming.ToolGuideTemplate ?? string.Empty,
            EphemeralTemplate = incoming.EphemeralTemplate ?? string.Empty,
            Scope = "global",
            UpdatedAtUtc =
                incoming.UpdatedAtUtc == default ? DateTime.UtcNow : incoming.UpdatedAtUtc,
        };

        CheckBlock("identity", doc.IdentityText);
        CheckBlock("runtime", doc.RuntimeTemplate);
        CheckBlock("cmdPython", doc.CmdPythonText);
        CheckBlock("toolGuide", doc.ToolGuideTemplate);
        CheckBlock("ephemeral", doc.EphemeralTemplate);
        CheckBlock("custom", doc.CustomText);

        int assembledLen = EstimateAssembledLength(doc);
        if (assembledLen > PromptDefaults.MaxAssembledChars)
        {
            throw new PromptConfigValidationException(
                "assembled",
                $"拼装后总长度 {assembledLen} 超过上限 {PromptDefaults.MaxAssembledChars}，请缩减各块文本"
            );
        }
        return doc;
    }

    private static void CheckBlock(string blockId, string text)
    {
        if ((text?.Length ?? 0) > PromptDefaults.MaxBlockChars)
        {
            throw new PromptConfigValidationException(
                blockId,
                $"提示词块 [{blockId}] 长度 {(text?.Length ?? 0)} 超过上限 {PromptDefaults.MaxBlockChars}"
            );
        }
    }

    private static int EstimateAssembledLength(PromptConfigDocument doc)
    {
        if (!doc.EnableHaloPrefix)
            return 0;
        int len = 0;
        int blocks = 0;
        void Add(string? t, bool on)
        {
            if (!on || string.IsNullOrWhiteSpace(t))
                return;
            len += (t ?? string.Empty).TrimEnd().Length;
            blocks++;
        }
        string custom = doc.CustomText ?? string.Empty;
        bool hasCustom = doc.EnableCustom && !string.IsNullOrWhiteSpace(custom);
        if (hasCustom)
        {
            len += custom.TrimEnd().Length;
            blocks++;
        }
        Add(doc.IdentityText, doc.EnableIdentity);
        Add(doc.RuntimeTemplate, doc.EnableRuntime);
        Add(doc.CmdPythonText, doc.EnableCmdPython);
        Add(doc.ToolGuideTemplate, doc.EnableToolGuide);
        Add(doc.EphemeralTemplate, doc.EnableEphemeral);
        if (blocks > 1)
            len += (blocks - 1) * 2;
        if (len > 0)
            len += 1;
        return len;
    }
}
