using System.Text.Json;
using HaloAgent.Engine.Abstractions;

namespace HaloAgent.Engine;

/// <summary>
/// Write-gate policy and output shaping for the tool station. Guards share one
/// frozen set of limits so cards, history and the model all see the same cuts:
/// display 2048, persisted history 8k, spill 64k, argument preview 4k.
/// </summary>
public static class EngineToolPolicy
{
    public const int DisplayLimit = 2048;
    public const int PersistLimit = 8192;
    public const int SpillThreshold = 65536;
    public const int PreviewLimit = 4096;

    public const int ExecMinTimeoutMs = 1;
    public const int ExecMaxTimeoutMs = 300000;
    public const int ExecDefaultTimeoutMs = 60000;

    private static readonly HashSet<string> WriteTools =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "write_file",
            "edit_file",
            "edit_lines",
            "edit",
            "undo_edit",
            "exec_command",
        };

    private static readonly HashSet<string> DeniedAsToolDenied =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "UNKNOWN_TOOL",
            "TOOL_NOT_ENABLED",
            "PATH_DENIED",
            "PATH_OUTSIDE_WORKSPACE",
            "COMMAND_NOT_ALLOWED",
            "COMMAND_TOO_LONG",
            "TOOL_DENIED",
        };

    private static readonly HashSet<string> DeniedAsTimeout =
        new(StringComparer.OrdinalIgnoreCase) { "TOOL_TIMEOUT", "INVALID_TIMEOUT", "TIMEOUT", };

    private static readonly HashSet<string> FrozenPassthrough =
        new(StringComparer.OrdinalIgnoreCase)
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

    /// <summary>Write operations pause for user approval; reads stream straight through.</summary>
    public static bool NeedsConfirmation(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && WriteTools.Contains(toolName.Trim());

    public static string PolicyOf(string? toolName) =>
        NeedsConfirmation(toolName) ? "confirm" : "direct";

    /// <summary>Short argument preview for cards and confirm payloads, capped at 4k.</summary>
    public static string BuildArgsPreview(string? argumentsJson)
    {
        string args = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson.Trim();
        if (args.Length <= PreviewLimit)
        {
            return args;
        }
        int omitted = args.Length - PreviewLimit;
        return args.Substring(0, PreviewLimit) + $" … [preview truncated {omitted} chars]";
    }

    /// <summary>Card and model facing text, capped at 2048 with an omission marker.</summary>
    public static string BuildDisplay(
        string output,
        string? spilledFile,
        out bool truncated,
        out int omittedChars
    )
    {
        string text = output ?? string.Empty;
        if (text.Length <= DisplayLimit)
        {
            truncated = false;
            omittedChars = 0;
            return text;
        }
        omittedChars = text.Length - DisplayLimit;
        truncated = true;
        string tail = string.IsNullOrWhiteSpace(spilledFile)
            ? "full text was truncated"
            : $"full text spilled to {spilledFile}";
        return text.Substring(0, DisplayLimit) + $"\n… [truncated {omittedChars} chars, {tail}]";
    }

    /// <summary>History facing text, capped at 8k with an omission marker.</summary>
    public static string BuildPersisted(string output)
    {
        string text = output ?? string.Empty;
        if (text.Length <= PersistLimit)
        {
            return text;
        }
        int omitted = text.Length - PersistLimit;
        return text.Substring(0, PersistLimit) + $"\n… [persisted {omitted} chars omitted]";
    }

    public static bool IsValidCallId(string? callId)
    {
        if (string.IsNullOrWhiteSpace(callId))
        {
            return false;
        }
        string trimmed = callId.Trim();
        if (trimmed.Length > 64)
        {
            return false;
        }
        foreach (char c in trimmed)
        {
            bool ok =
                (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9')
                || c == '_'
                || c == '-';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Maps a tool-level error code onto the frozen contract code without ever
    /// leaking dispatch misses into NOT_FOUND, which stays reserved for the
    /// cutover chain.
    /// </summary>
    public static string ContractCodeOf(string? errorCode)
    {
        string code = (errorCode ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code))
        {
            return "INTERNAL";
        }
        if (DeniedAsToolDenied.Contains(code))
        {
            return "TOOL_DENIED";
        }
        if (DeniedAsTimeout.Contains(code))
        {
            return "TOOL_TIMEOUT";
        }
        if (FrozenPassthrough.Contains(code))
        {
            return code;
        }
        return "INTERNAL";
    }

    /// <summary>Contract envelope for a tool failure: frozen code plus the specific detail.</summary>
    public static string BuildErrorPayload(string? errorCode, string? detail)
    {
        try
        {
            return JsonSerializer.Serialize(
                new
                {
                    contract = ContractCodeOf(errorCode),
                    detail = (errorCode ?? string.Empty).Trim(),
                    message = (detail ?? string.Empty).Trim(),
                },
                HaloJson.Options
            );
        }
        catch
        {
            return "{}";
        }
    }
}
