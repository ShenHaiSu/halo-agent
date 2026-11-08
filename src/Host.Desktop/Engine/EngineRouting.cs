namespace Host.Desktop.Engine;

/// <summary>
/// Cutover batches for the frame boundary. Each batch flips independently
/// between the legacy direct-call chain (default, safe to roll back) and the
/// engine-owned path, so a risky batch can be reverted without touching the
/// others. The frontend whole-chain switch stays the outer gate: when it is
/// off, none of these batches receive traffic regardless of the values here.
/// </summary>
public sealed class EngineRouting
{
    public const string BatchSessionTurns = "A";
    public const string BatchConfigUsage = "B";
    public const string BatchToolAssets = "C";

    public const string EnvironmentVariable = "HALO_ENGINE_ROUTING";

    private static readonly IReadOnlySet<string> SessionTurnMethods = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "session.new",
        "session.resume",
        "session.rename",
        "session.delete",
        "session.clear",
        "session.list",
        "session.get",
        "session.history",
        "session.compact",
        "session.setCwd",
        "agent.prompt",
        "agent.queue",
        "agent.cancel",
        "agent.retry",
        "agent.drain",
        "agent.answer",
        "agent.unqueue",
        "state.snapshot",
        "state.mode",
        "state.persona",
        "state.maxTurns",
        "tool.undoRollback",
    };

    private static readonly IReadOnlySet<string> ConfigUsageMethods = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "system.ping",
        "system.info",
        "settings.saveProviders",
        "settings.setActive",
        "settings.saveNetwork",
        "settings.saveBehavior",
        "asset.personas.list",
        "asset.personas.get",
        "usage.query",
        "usage.clear",
    };

    private static readonly IReadOnlySet<string> ToolAssetMethods = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "asset.skills.list",
        "asset.skills.get",
        "asset.memory.list",
        "asset.memory.save",
    };

    private static readonly IReadOnlySet<string> HostOnlyMethods = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "host.pickDirectory",
        "host.openPath",
    };

    private readonly HashSet<string> _enabledBatches;

    public EngineRouting(IEnumerable<string>? enabledBatches = null)
    {
        _enabledBatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (enabledBatches is not null)
        {
            foreach (string batch in enabledBatches)
            {
                string normalized = batch.Trim().ToUpperInvariant();
                if (
                    normalized == BatchSessionTurns
                    || normalized == BatchConfigUsage
                    || normalized == BatchToolAssets
                )
                {
                    _enabledBatches.Add(normalized);
                }
            }
        }
    }

    public bool IsBatchEnabled(string batch) => _enabledBatches.Contains(batch);

    public bool IsHostOnly(string method) => HostOnlyMethods.Contains(method);

    /// <summary>
    /// True when the named method should be served by the engine on this host.
    /// Host-only capabilities always stay on the shell and never enter the engine.
    /// </summary>
    public bool RoutesToEngine(string method)
    {
        if (string.IsNullOrWhiteSpace(method) || IsHostOnly(method))
        {
            return false;
        }
        if (SessionTurnMethods.Contains(method))
        {
            return _enabledBatches.Contains(BatchSessionTurns);
        }
        if (ConfigUsageMethods.Contains(method))
        {
            return _enabledBatches.Contains(BatchConfigUsage);
        }
        if (ToolAssetMethods.Contains(method))
        {
            return _enabledBatches.Contains(BatchToolAssets);
        }
        return false;
    }

    public IReadOnlySet<string> EnabledBatches => _enabledBatches;

    /// <summary>
    /// Reads the process configuration. Accepted values are comma-separated
    /// batch letters (for example "A,C"), "all", or anything else for the
    /// legacy default. Missing or unreadable configuration means all legacy.
    /// </summary>
    public static EngineRouting FromEnvironment()
    {
        string? raw;
        try
        {
            raw = Environment.GetEnvironmentVariable(EnvironmentVariable);
        }
        catch
        {
            return new EngineRouting();
        }
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new EngineRouting();
        }
        string normalized = raw.Trim().ToUpperInvariant();
        if (normalized == "ALL")
        {
            return new EngineRouting(
                new[] { BatchSessionTurns, BatchConfigUsage, BatchToolAssets }
            );
        }
        string[] parts = normalized.Split(
            new[] { ',', ';', ' ', '|' },
            StringSplitOptions.RemoveEmptyEntries
        );
        return new EngineRouting(parts);
    }
}
