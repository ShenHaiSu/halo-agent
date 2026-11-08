namespace HaloAgent.Engine.Abstractions;

/// <summary>
/// Frozen subset catalog mirrored from the frame schema file.
/// The schema file stays the single source of truth; this mirror lets
/// engine code reference names without parsing JSON on every call.
/// After the freeze only optional payload fields may grow.
/// </summary>
public static class ContractCatalog
{
    public const string ContractVersion = "0.1.1";

    public static readonly IReadOnlyList<string> Methods = new[]
    {
        "system.ping",
        "system.info",
        "session.new",
        "session.resume",
        "session.rename",
        "session.title",
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
        "settings.saveProviders",
        "settings.setActive",
        "settings.saveNetwork",
        "settings.saveBehavior",
        "asset.skills.list",
        "asset.skills.get",
        "asset.memory.list",
        "asset.memory.save",
        "asset.personas.list",
        "asset.personas.get",
        "usage.query",
        "usage.clear",
        "host.pickDirectory",
        "host.openPath",
    };

    public static readonly IReadOnlyList<string> Events = new[]
    {
        "turn.started",
        "text.delta",
        "text.done",
        "tool.start",
        "tool.delta",
        "tool.end",
        "tool.confirm",
        "turn.done",
        "turn.failed",
        "turn.cancelled",
        "session.updated",
        "session.compacted",
        "state.changed",
        "usage.tick",
    };

    public static readonly IReadOnlyList<string> ErrorCodes = new[]
    {
        "INVALID_PARAMS",
        "NOT_FOUND",
        "TURN_IN_PROGRESS",
        "COMPACTING",
        "RATE_LIMITED",
        "PROVIDER_AUTH",
        "NETWORK_FAIL",
        "TOOL_DENIED",
        "TOOL_TIMEOUT",
        "CANCELLED",
        "INTERNAL",
    };
}
