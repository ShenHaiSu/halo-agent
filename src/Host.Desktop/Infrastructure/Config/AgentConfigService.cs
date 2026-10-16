using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Infrastructure.Config;

/// <summary>
/// need08：智能体行为配置服务（config/agent.json + 原子写 + 启动初始化）。
/// 逐行对标 PromptConfigService.cs；失败回默认不断会话。
/// </summary>
public sealed class AgentConfigService
{
    private readonly StoragePathManager _storagePathManager;
    private readonly object _syncLock = new();

    public string AgentConfigFilePath => Path.Combine(_storagePathManager.ConfigDirectory, "agent.json");

    public AgentConfigService(StoragePathManager storagePathManager)
    {
        _storagePathManager = storagePathManager ?? throw new ArgumentNullException(nameof(storagePathManager));
        EnsureInitialized();
    }

    public void EnsureInitialized()
    {
        lock (_syncLock)
        {
            Directory.CreateDirectory(_storagePathManager.ConfigDirectory);
            if (!File.Exists(AgentConfigFilePath))
            {
                var def = CreateDefault();
                AtomicFileWriter.WriteJsonAtomic(AgentConfigFilePath, def);
            }
        }
    }

    public AgentConfigDocument GetInternal() => LoadWithMeta().Document;

    public (AgentConfigDocument Document, bool IsDefault, string? FallbackReason) LoadWithMeta() => LoadInternal();

    public StoragePathManager StorageManager => _storagePathManager;

    public AgentConfigDocument SaveAgentConfig(AgentConfigDocument incoming)
    {
        if (incoming == null) throw new ArgumentNullException(nameof(incoming));
        lock (_syncLock)
        {
            var normalized = NormalizeAndValidate(incoming, forSave: true);
            normalized.UpdatedAtUtc = DateTime.UtcNow;
            if (File.Exists(AgentConfigFilePath))
            {
                try
                {
                    string bakPath = AgentConfigFilePath + ".bak";
                    File.Copy(AgentConfigFilePath, bakPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    HaloLogger.App.Warn("[Agent] agent.json.bak 备份失败（继续保存）", ex);
                }
            }
            AtomicFileWriter.WriteJsonAtomic(AgentConfigFilePath, normalized);
            HaloLogger.App.Info("[Agent] agent.json 已保存（原子写盘）。");
            return Clone(normalized);
        }
    }

    /// <summary>
    /// 按项/全部恢复默认。空/all/default → 全量；主 ID + 别名见 01 §4.3；未知抛 AGENT_VALIDATION。
    /// </summary>
    public AgentConfigDocument ResetAgentConfig(string? field)
    {
        lock (_syncLock)
        {
            var def = CreateDefault();
            if (string.IsNullOrWhiteSpace(field)
                || string.Equals(field.Trim(), "all", StringComparison.OrdinalIgnoreCase)
                || string.Equals(field.Trim(), "default", StringComparison.OrdinalIgnoreCase))
            {
                return SaveAgentConfig(def);
            }
            string id = field.Trim().ToLowerInvariant();
            var (current, _, _) = LoadInternal();
            switch (id)
            {
                case "defaulttimeout":
                case "execdefaulttimeoutms":
                case "default":
                case "default-timeout":
                case "default_timeout":
                    current.ExecDefaultTimeoutMs = AgentDefaults.DefaultTimeoutMs;
                    break;
                case "maxtimeout":
                case "execmaxtimeoutms":
                case "max":
                case "max-timeout":
                case "max_timeout":
                    current.ExecMaxTimeoutMs = AgentDefaults.MaxTimeoutMs;
                    break;
                case "iterations":
                case "maxiterations":
                case "rounds":
                case "max-iterations":
                case "max_iterations":
                    current.MaxIterations = AgentDefaults.MaxIterations;
                    break;
                default:
                    throw new AgentConfigValidationException("field", $"未知恢复项：{field.Trim()}");
            }
            return SaveAgentConfig(current);
        }
    }

    private (AgentConfigDocument Document, bool IsDefault, string? FallbackReason) LoadInternal()
    {
        string path = AgentConfigFilePath;
        if (!File.Exists(path))
        {
            var def = CreateDefault();
            try { AtomicFileWriter.WriteJsonAtomic(path, def); } catch { /* ignore */ }
            return (Clone(def), true, null);
        }
        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[Agent] agent.json 读取失败，已回退默认", ex);
            return (CreateDefault(), true, "agent.json 读取失败，已回退默认");
        }
        if (string.IsNullOrWhiteSpace(raw))
        {
            HaloLogger.App.Warn("[Agent] agent.json 为空，已回退默认");
            return (CreateDefault(), true, "agent.json 为空，已回退默认");
        }
        AgentConfigDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<AgentConfigDocument>(raw, HaloJsonOptions.IndentedCamelCase);
        }
        catch (Exception ex)
        {
            string corruptPath = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            try { File.Copy(path, corruptPath, overwrite: false); } catch { /* ignore */ }
            HaloLogger.App.Warn("[Agent] agent.json 损坏，已回退默认（原文件保留为 " + corruptPath + "）", ex);
            return (CreateDefault(), true, "agent.json 损坏，已回退默认（原文件保留为 agent.json.corrupt-<timestamp>）");
        }
        if (doc == null)
        {
            return (CreateDefault(), true, "agent.json 解析为空，已回退默认");
        }
        try
        {
            var normalized = NormalizeAndValidate(doc, forSave: false);
            return (normalized, false, null);
        }
        catch (AgentConfigValidationException vex)
        {
            HaloLogger.App.Warn($"[Agent] agent.json 配置超限（字段 {vex.Field}），已回退默认: {vex.Message}");
            var def = CreateDefault();
            return (def, true, $"agent.json 配置超限（字段 {vex.Field}），已回退默认");
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[Agent] agent.json 校验失败，已回退默认", ex);
            return (CreateDefault(), true, "agent.json 校验失败，已回退默认");
        }
    }

    private static AgentConfigDocument CreateDefault() => new()
    {
        Version = 1,
        ExecDefaultTimeoutMs = AgentDefaults.DefaultTimeoutMs,
        ExecMaxTimeoutMs = AgentDefaults.MaxTimeoutMs,
        MaxIterations = AgentDefaults.MaxIterations,
        Scope = "global",
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static AgentConfigDocument Clone(AgentConfigDocument src)
    {
        string json = JsonSerializer.Serialize(src, HaloJsonOptions.IndentedCamelCase);
        return JsonSerializer.Deserialize<AgentConfigDocument>(json, HaloJsonOptions.IndentedCamelCase)
            ?? CreateDefault();
    }

    internal static AgentConfigDocument NormalizeAndValidate(AgentConfigDocument incoming, bool forSave)
    {
        var doc = new AgentConfigDocument
        {
            Version = 1,
            ExecDefaultTimeoutMs = incoming.ExecDefaultTimeoutMs,
            ExecMaxTimeoutMs = incoming.ExecMaxTimeoutMs,
            MaxIterations = incoming.MaxIterations,
            Scope = "global",
            UpdatedAtUtc = incoming.UpdatedAtUtc == default ? DateTime.UtcNow : incoming.UpdatedAtUtc,
        };

        if (doc.ExecDefaultTimeoutMs < 0 || (doc.ExecDefaultTimeoutMs > 0 && doc.ExecDefaultTimeoutMs > AgentDefaults.HardTimeoutCeilMs))
        {
            throw new AgentConfigValidationException("execDefaultTimeoutMs",
                $"默认超时非法：{doc.ExecDefaultTimeoutMs}（允许 0 或 1..{AgentDefaults.HardTimeoutCeilMs}）");
        }
        if (doc.ExecMaxTimeoutMs < 0 || (doc.ExecMaxTimeoutMs > 0 && doc.ExecMaxTimeoutMs > AgentDefaults.HardTimeoutCeilMs))
        {
            throw new AgentConfigValidationException("execMaxTimeoutMs",
                $"上限超时非法：{doc.ExecMaxTimeoutMs}（允许 0 或 1..{AgentDefaults.HardTimeoutCeilMs}）");
        }
        if (doc.MaxIterations < 0 || doc.MaxIterations > AgentDefaults.HardIterationsCeil)
        {
            throw new AgentConfigValidationException("maxIterations",
                $"最大轮次非法：{doc.MaxIterations}（允许 0 或 1..{AgentDefaults.HardIterationsCeil}）");
        }
        if (doc.ExecMaxTimeoutMs > 0 && doc.ExecDefaultTimeoutMs > doc.ExecMaxTimeoutMs)
        {
            throw new AgentConfigValidationException("execDefaultTimeoutMs",
                $"默认超时不得大于上限（默认 {doc.ExecDefaultTimeoutMs} > 上限 {doc.ExecMaxTimeoutMs}），请调大上限或调小默认");
        }
        return doc;
    }
}
