using HaloAgent.Engine.Abstractions;

namespace HaloAgent.Engine;

/// <summary>
/// Skeleton method table for the frozen subset. System and snapshot entries
/// answer from memory; turn and session entries echo a recorded turn so the
/// mock client can drive the main flow; everything else reports a typed stub.
/// Full logic (agent loop, storage, gateway) lands when the engine sinks.
/// </summary>
internal static class SkeletonRoutes
{
    public static void RegisterInto(Dictionary<string, FrameHandler> routes)
    {
        routes["system.ping"] = (request, context, token) =>
            Task.FromResult(ResponseFrame.Success(request.Id, new { pong = true }));

        routes["system.info"] = (request, context, token) =>
            Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        version = ContractCatalog.ContractVersion,
                        queueBound = FrameServer.QueueBound,
                        journalCapacity = context.Events.Capacity,
                    }
                )
            );

        routes["state.snapshot"] = (request, context, token) =>
        {
            var state = context.State;
            return Task.FromResult(
                ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        activeSessionId = state.ActiveSessionId,
                        mode = state.Mode,
                        personaId = state.PersonaId,
                        maxTurns = state.MaxTurns,
                        latestSequence = context.Events.LatestSequence,
                    }
                )
            );
        };

        routes["state.mode"] = (request, context, token) =>
        {
            string mode = FrameServer.ReadString(request.Payload, "mode");
            if (!string.IsNullOrWhiteSpace(mode))
            {
                context.State.Mode = mode;
            }
            return Task.FromResult(
                ResponseFrame.Success(request.Id, new { mode = context.State.Mode })
            );
        };

        routes["session.new"] = (request, context, token) =>
        {
            string title = FrameServer.ReadString(request.Payload, "title");
            string sessionId = Guid.NewGuid().ToString("N");
            context.State.TryRememberSession(
                sessionId,
                string.IsNullOrWhiteSpace(title) ? "New session" : title
            );
            context.State.ActiveSessionId = sessionId;
            context.Events.Append("session.updated", new { sessionId, reason = "created" });
            return Task.FromResult(ResponseFrame.Success(request.Id, new { sessionId }));
        };

        routes["session.list"] = (request, context, token) =>
        {
            var items = context
                .State.Sessions.Select(entry => new { sessionId = entry.Key, title = entry.Value })
                .ToList();
            return Task.FromResult(ResponseFrame.Success(request.Id, new { sessions = items }));
        };

        routes["agent.prompt"] = (request, context, token) =>
        {
            string sessionId = FrameServer.ReadString(request.Payload, "sessionId");
            string text = FrameServer.ReadString(request.Payload, "text");
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(text))
            {
                return Task.FromResult(
                    ResponseFrame.Fail(
                        request.Id,
                        "INVALID_PARAMS",
                        "sessionId and text are required."
                    )
                );
            }
            string turnId = Guid.NewGuid().ToString("N");
            context.State.TrackTurn(turnId, sessionId);
            context.State.EnsureTurnScope(turnId);
            context.Events.Append("turn.started", new { turnId, sessionId });
            context.Events.Append("text.delta", new { turnId, delta = text });
            context.Events.Append("text.done", new { turnId, length = text.Length });
            context.Events.Append("turn.done", new { turnId, sessionId });
            return Task.FromResult(ResponseFrame.Success(request.Id, new { turnId }));
        };

        foreach (string method in ContractCatalog.Methods)
        {
            if (!routes.ContainsKey(method))
            {
                string captured = method;
                routes[captured] = (request, context, token) =>
                    Task.FromResult(
                        ResponseFrame.Success(request.Id, new { stub = true, method = captured })
                    );
            }
        }
    }
}
