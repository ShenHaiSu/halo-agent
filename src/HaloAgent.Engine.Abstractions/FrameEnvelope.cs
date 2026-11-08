using System.Text.Json;

namespace HaloAgent.Engine.Abstractions;

/// <summary>
/// Frame envelope shapes for the object channel inside the engine.
/// Only the host boundary serializes these to JSON string batches;
/// inside the engine they travel as objects to avoid framing overhead.
/// Surplus payload fields are accepted so future optional additions stay compatible.
/// </summary>
public enum FrameKind
{
    Request,
    Response,
    Event,
}

/// <summary>Error payload carried by a failed response frame.</summary>
public sealed record ErrorBody(string Code, string Message, string? Hint = null);

/// <summary>Inbound call frame: one method invocation with a numeric id.</summary>
public sealed record RequestFrame(long Id, string Method, JsonElement Payload)
{
    public static RequestFrame WithPayload(long id, string method, object? payload = null)
    {
        JsonElement element = payload is null
            ? JsonDocument.Parse("{}").RootElement
            : JsonSerializer.SerializeToElement(payload, HaloJson.Options);
        return new RequestFrame(id, method, element);
    }
}

/// <summary>Outbound reply frame: success result or typed error.</summary>
public sealed record ResponseFrame(
    long Id,
    bool Ok,
    JsonElement? Result = null,
    ErrorBody? Error = null
)
{
    public static ResponseFrame Success(long id, object? result = null)
    {
        JsonElement? element = result is null
            ? null
            : JsonSerializer.SerializeToElement(result, HaloJson.Options);
        return new ResponseFrame(id, true, element, null);
    }

    public static ResponseFrame Fail(long id, string code, string message, string? hint = null) =>
        new(id, false, null, new ErrorBody(code, message, hint));
}

/// <summary>Sequenced broadcast frame published through the event journal.</summary>
public sealed record EventFrame(long Sequence, string Kind, JsonElement Payload);
