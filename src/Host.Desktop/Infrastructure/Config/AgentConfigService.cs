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
                // need13：子代理按项恢复
                 case "turntimeout":
                 case "turntimeoutms":
                 case "turn-timeout":
                 case "turn_timeout":
                     current.TurnTimeoutMs = AgentDefaults.TurnTimeoutMs;
                     break;
                 case "subbarrierbuffer":
                 case "subbarrierbufferms":
                 case "barrierbuffer":
                     current.SubBarrierBufferMs = AgentDefaults.SubBarrierBufferMs;
                     break;
                case "subenabled":
                case "subagentenabled":
                case "sub-enabled":
                    current.SubAgentEnabled = true;
                    break;
                case "subparallel":
                case "submaxparallel":
                case "sub-parallel":
                    current.SubMaxParallel = AgentDefaults.SubMaxParallelDefault;
                    break;
                case "subiterations":
                case "submaxiterations":
                case "sub-iterations":
                    current.SubMaxIterations = AgentDefaults.SubMaxIterationsDefault;
                    break;
                case "subperms":
                case "sub-perms":
                case "subpermissions":
                    current.SubAllowExecCommand = true;
                    current.SubAllowFileWrite = true;
                    current.SubAllowSkillWrite = false;
                    current.SubAllowMemoryWrite = false;
                    current.SubAllowAskUser = false;
                    break;
                // need17：strict/tools 按项恢复
                case "stricttoolsmode":
                case "strict":
                case "strict-tools":
                    current.StrictToolsMode = Host.Desktop.Core.Models.StrictToolsMode.Prefer;
                    break;
                case "toolspreset":
                case "tools":
                case "toolpreset":
                    current.ToolsPreset = null;
                    current.ToolsAllowlist = null;
                    current.ToolsDenylist = null;
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
         TurnTimeoutMs = AgentDefaults.TurnTimeoutMs,
         SubBarrierBufferMs = AgentDefaults.SubBarrierBufferMs,
        SubAgentEnabled = true,
        SubMaxParallel = AgentDefaults.SubMaxParallelDefault,
        SubMaxIterations = AgentDefaults.SubMaxIterationsDefault,
        SubAllowExecCommand = true,
        SubAllowFileWrite = true,
        SubAllowSkillWrite = false,
        SubAllowMemoryWrite = false,
        SubAllowAskUser = false,
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
            SubAgentEnabled = incoming.SubAgentEnabled,
            // need13：老文件缺字段反序列化为 0 → 读时回默认（不算损坏）；保存时 0 保持走校验拒绝
            SubMaxParallel = incoming.SubMaxParallel == 0 && !forSave ? AgentDefaults.SubMaxParallelDefault : incoming.SubMaxParallel,
            SubMaxIterations = incoming.SubMaxIterations == 0 && !forSave ? AgentDefaults.SubMaxIterationsDefault : incoming.SubMaxIterations,
            SubAllowExecCommand = incoming.SubAllowExecCommand,
             // need22-P1 T3/T4：新超时字段透传（老文件缺失=0，读时回默认；保存时 0 同样回默认后校验）。
             TurnTimeoutMs = incoming.TurnTimeoutMs <= 0 ? AgentDefaults.TurnTimeoutMs : incoming.TurnTimeoutMs,
             SubBarrierBufferMs = incoming.SubBarrierBufferMs <= 0 ? AgentDefaults.SubBarrierBufferMs : incoming.SubBarrierBufferMs,
            SubAllowFileWrite = incoming.SubAllowFileWrite,
            SubAllowSkillWrite = incoming.SubAllowSkillWrite,
            SubAllowMemoryWrite = incoming.SubAllowMemoryWrite,
            SubAllowAskUser = incoming.SubAllowAskUser,
            // need17：新字段透传（老文件缺失→默认；非法值读时回默认、存时拒绝，见下）。
            StrictToolsMode = incoming.StrictToolsMode,
            ToolsPreset = string.IsNullOrWhiteSpace(incoming.ToolsPreset) ? null : incoming.ToolsPreset.Trim(),
            ToolsAllowlist = incoming.ToolsAllowlist == null ? null : incoming.ToolsAllowlist.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList(),
            ToolsDenylist = incoming.ToolsDenylist == null ? null : incoming.ToolsDenylist.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList(),
            AllowedCommands = incoming.AllowedCommands == null ? null : incoming.AllowedCommands.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList(),
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
        // need22-P1 T3/T4：单轮总体超时与 Barrier buffer 范围校验（超限存时拒绝、读时回默认由 LoadInternal 兜底）。
        if (doc.TurnTimeoutMs < AgentDefaults.TurnTimeoutMinMs || doc.TurnTimeoutMs > AgentDefaults.HardTimeoutCeilMs)
        {
            throw new AgentConfigValidationException("turnTimeoutMs",
                $"单轮总体超时非法：{doc.TurnTimeoutMs}（允许 {AgentDefaults.TurnTimeoutMinMs}..{AgentDefaults.HardTimeoutCeilMs} 毫秒，默认 {AgentDefaults.TurnTimeoutMs} 即 10min）");
        }
        if (doc.SubBarrierBufferMs < 0 || doc.SubBarrierBufferMs > AgentDefaults.HardTimeoutCeilMs)
        {
            throw new AgentConfigValidationException("subBarrierBufferMs",
                $"Barrier 超时余量非法：{doc.SubBarrierBufferMs}（允许 0..{AgentDefaults.HardTimeoutCeilMs} 毫秒）");
        }
        // need13：子代理校验（老文件缺字段已在上回默认；此处只判显式超限）
        if (doc.SubMaxParallel < 1 || doc.SubMaxParallel > AgentDefaults.SubHardParallelCeil)
        {
            throw new AgentConfigValidationException("subMaxParallel",
                $"子代理并发非法：{doc.SubMaxParallel}（允许 1..{AgentDefaults.SubHardParallelCeil}）");
        }
        if (doc.SubMaxIterations < 1 || doc.SubMaxIterations > AgentDefaults.SubHardIterationsCeil)
        {
            throw new AgentConfigValidationException("subMaxIterations",
                $"子代理轮次非法：{doc.SubMaxIterations}（允许 1..{AgentDefaults.SubHardIterationsCeil}）");
        }
        // v1 锁定：true 归一为 false + Warn（不抛错，保证手改不炸会话）
        if (doc.SubAllowAskUser)
        {
            doc.SubAllowAskUser = false;
            HaloLogger.App.Warn("[Agent] subAllowAskUser=true 已归一为 false（v1 锁定子禁问）");
        }
        // need17-01：strict/tools 校验（读时非法回默认由 LoadInternal 兜底；存时抛错阻断）。
        if (!Enum.IsDefined(typeof(Host.Desktop.Core.Models.StrictToolsMode), doc.StrictToolsMode))
        {
            if (forSave)
            {
                throw new AgentConfigValidationException("strictToolsMode",
                    $"网关 strict 模式非法：{(int)doc.StrictToolsMode}（可选值：Prefer | Require | Off）。请修正 agent.json 后重试。");
            }
            doc.StrictToolsMode = Host.Desktop.Core.Models.StrictToolsMode.Prefer;
        }
        // need17-01：preset/allowlist/denylist 互斥校验（非法抛错；读时由 LoadInternal 回默认，存时阻断）。
        Host.Desktop.Tools.ToolPreset.ValidateOrThrow(doc.ToolsPreset, doc.ToolsAllowlist, doc.ToolsDenylist);
        doc.Scope = "global"; doc.Version = 1;
        return doc;
    }
}
