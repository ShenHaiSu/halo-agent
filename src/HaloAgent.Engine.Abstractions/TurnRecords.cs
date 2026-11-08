using System.Text.Json;

namespace HaloAgent.Engine.Abstractions;

/// <summary>Turn lifecycle states owned by the engine.</summary>
public enum TurnOutcome
{
    Running,
    AwaitingConfirmation,
    Done,
    Failed,
    Cancelled,
}

/// <summary>One agent turn: links the inbound request with session and outcome.</summary>
public sealed record TurnRecord(
    string TurnId,
    string SessionId,
    long RequestId,
    TurnOutcome Outcome,
    DateTimeOffset StartedAt
);

/// <summary>Tool confirmation gate: engine pauses the turn until an answer arrives.</summary>
public sealed record ConfirmationRequest(
    string TurnId,
    string ToolCallId,
    string ToolName,
    JsonElement Args,
    DateTimeOffset RaisedAt
);
