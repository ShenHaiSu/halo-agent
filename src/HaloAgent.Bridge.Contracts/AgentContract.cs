namespace HaloAgent.Bridge.Contracts;

/// <summary>
/// Frozen frame-contract catalog: the single C# mirror of
/// contract/agent.schema.json (method names, event names, error codes).
/// The schema file stays the source of truth; this class lets backend code
/// and consistency tests reference the frozen subset without parsing JSON
/// on every call. After the freeze only optional payload fields may grow;
/// names and semantics listed here must not change.
/// </summary>
public static class AgentContract
{
    public const string ContractVersion = "0.1.0";

    public static readonly IReadOnlyList<string> Methods = new[]
    {
        "system.ping",
        "system.info",
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

    /// <summary>
    /// Lightweight frame-shape check used at the managed boundary.
    /// Malformed input reports false so the caller can log and skip it
    /// without breaking the read loop; surplus fields are accepted so that
    /// future optional additions stay backward compatible.
    /// </summary>
    public static bool TryClassifyFrame(string? json, out string kind)
    {
        kind = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return false;
            }

            if (
                !doc.RootElement.TryGetProperty("t", out var tag)
                || tag.ValueKind != System.Text.Json.JsonValueKind.String
            )
            {
                return false;
            }

            switch (tag.GetString())
            {
                case "req":
                    if (!HasInt(doc.RootElement, "id") || !HasString(doc.RootElement, "m"))
                    {
                        return false;
                    }

                    kind = "req";
                    return true;
                case "res":
                    if (!HasInt(doc.RootElement, "id") || !HasBool(doc.RootElement, "ok"))
                    {
                        return false;
                    }

                    kind = "res";
                    return true;
                case "evt":
                    if (!HasInt(doc.RootElement, "seq") || !HasString(doc.RootElement, "k"))
                    {
                        return false;
                    }

                    kind = "evt";
                    return true;
                default:
                    return false;
            }
        }
    }

    private static bool HasString(System.Text.Json.JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String;
    }

    private static bool HasInt(System.Text.Json.JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.Number
            && value.TryGetInt32(out _);
    }

    private static bool HasBool(System.Text.Json.JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value)
            && (
                value.ValueKind == System.Text.Json.JsonValueKind.True
                || value.ValueKind == System.Text.Json.JsonValueKind.False
            );
    }
}
