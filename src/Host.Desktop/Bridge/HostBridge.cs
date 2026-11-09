using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using HaloAgent.Engine.Abstractions;
using Host.Desktop.Engine;

namespace Host.Desktop.Bridge;

/// <summary>
/// Converged frame entry exposed to the web client next to the legacy
/// direct-call facade. The boundary speaks JSON strings only: complex
/// generics stay inside the process because the embedded browser binding
/// is unstable for nested generic shapes. Reads never touch disk, and the
/// cancel entry never waits on the engine write gate, so stopping a turn
/// stays the fastest path through the shell.
/// </summary>
public sealed class HostBridge
{
    private readonly InProcChannel _channel;
    private readonly EngineRouting _routing;
    private Form? _form;

    public HostBridge(InProcChannel channel, EngineRouting? routing = null, Form? form = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _routing = routing ?? new EngineRouting();
        _form = form;
    }

    public void AttachForm(Form form)
    {
        _form ??= form;
    }

    /// <summary>
    /// Single method entry for the web client. Never throws: malformed
    /// envelopes are answered with a typed error frame so one bad frame
    /// cannot break the connection for later calls.
    /// </summary>
    public async Task<string> invoke(string requestJson)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
        {
            return FailResponse(0, "INVALID_PARAMS", "Request body is required.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(requestJson);
        }
        catch (JsonException)
        {
            return FailResponse(0, "INVALID_PARAMS", "Request body is not valid JSON.");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return FailResponse(0, "INVALID_PARAMS", "Request envelope must be an object.");
            }

            long id = ReadId(root);
            string method = ReadString(root, "m");
            if (string.IsNullOrWhiteSpace(method))
            {
                return FailResponse(id, "INVALID_PARAMS", "Method name is required.");
            }

            JsonElement payload =
                root.TryGetProperty("p", out var node) && node.ValueKind == JsonValueKind.Object
                    ? node
                    : JsonDocument.Parse("{}").RootElement;

            if (string.Equals(method, "system.ping", StringComparison.Ordinal))
            {
                return SuccessResponse(id, new JsonObject { ["pong"] = true });
            }

            if (string.Equals(method, "system.info", StringComparison.Ordinal))
            {
                return SuccessResponse(id, BuildSystemInfo());
            }

            if (_routing.IsHostOnly(method))
            {
                return await HandleHostOnlyAsync(id, method, payload).ConfigureAwait(false);
            }

            if (!_routing.RoutesToEngine(method))
            {
                return FailResponse(
                    id,
                    "NOT_FOUND",
                    $"Method '{method}' is served by the legacy chain on this host.",
                    "Flip the matching cutover batch to route it through the engine."
                );
            }

            ResponseFrame response;
            try
            {
                var request = new RequestFrame(id, method, payload);
                response = await _channel.InvokeAsync(request).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return FailResponse(id, "CANCELLED", "Request was cancelled.");
            }
            catch (ObjectDisposedException)
            {
                return FailResponse(id, "INTERNAL", "The engine channel is shutting down.");
            }
            catch (Exception ex)
            {
                return FailResponse(id, "INTERNAL", "Handler failed.", ex.Message);
            }

            return SerializeResponse(response);
        }
    }

    /// <summary>
    /// Batched event read for the polling loops. Pure memory access: no disk,
    /// no network, no engine round-trip beyond the in-memory ring, so the
    /// bridge thread is never held by this call.
    /// </summary>
    public Task<string> getEventBatch(long sinceSeq, int maxCount)
    {
        RingBatch batch;
        try
        {
            batch = _channel.Ring.Read(sinceSeq, maxCount);
        }
        catch (Exception ex)
        {
            return Task.FromResult(FailResponse(0, "INTERNAL", "Event read failed.", ex.Message));
        }

        var frames = new JsonArray();
        foreach (EventFrame frame in batch.Frames)
        {
            frames.Add(
                new JsonObject
                {
                    ["t"] = "evt",
                    ["seq"] = frame.Sequence,
                    ["k"] = frame.Kind,
                    ["p"] = JsonNode.Parse(frame.Payload.GetRawText()),
                }
            );
        }

        var document = new JsonObject
        {
            ["frames"] = frames,
            ["nextSeq"] = batch.NextSequence,
            ["hasGap"] = batch.HasGap,
            ["droppedEstimate"] = batch.DroppedEstimate,
        };
        return Task.FromResult(document.ToJsonString(HaloJson.Options));
    }

    private async Task<string> HandleHostOnlyAsync(long id, string method, JsonElement payload)
    {
        try
        {
            if (string.Equals(method, "host.pickDirectory", StringComparison.Ordinal))
            {
                string? picked = await PickDirectoryOnUiThreadAsync().ConfigureAwait(false);
                if (picked is null)
                {
                    return FailResponse(id, "CANCELLED", "Directory picking was cancelled.");
                }
                var result = new JsonObject { ["path"] = picked };
                return SuccessResponse(id, result);
            }

            if (string.Equals(method, "host.openPath", StringComparison.Ordinal))
            {
                string path = ReadString(payload, "path");
                if (string.IsNullOrWhiteSpace(path))
                {
                    return FailResponse(id, "INVALID_PARAMS", "path is required.");
                }
                OpenPathWithShell(path);
                var result = new JsonObject { ["opened"] = true };
                return SuccessResponse(id, result);
            }

            return FailResponse(id, "NOT_FOUND", $"Unknown host capability '{method}'.");
        }
        catch (Exception ex)
        {
            return FailResponse(id, "INTERNAL", "Host capability failed.", ex.Message);
        }
    }

    private Task<string?> PickDirectoryOnUiThreadAsync()
    {
        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Form? form = _form;
        if (form is null || form.IsDisposed)
        {
            completion.SetResult(null);
            return completion.Task;
        }

        void Pick()
        {
            try
            {
                using var dialog = new FolderBrowserDialog();
                completion.SetResult(
                    dialog.ShowDialog(form) == DialogResult.OK ? dialog.SelectedPath : null
                );
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }

        if (form.InvokeRequired)
        {
            try
            {
                form.BeginInvoke(new Action(Pick));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }
        else
        {
            Pick();
        }
        return completion.Task;
    }

    private static void OpenPathWithShell(string path)
    {
        var start = new ProcessStartInfo { FileName = path, UseShellExecute = true, };
        Process.Start(start);
    }

    /// <summary>
    /// Capability probe answered by the shell itself so the web client can
    /// explain why an area stays on the legacy chain even when its batch is off.
    /// </summary>
    private JsonObject BuildSystemInfo()
    {
        var batches = new JsonArray();
        var batchesAlias = new JsonArray();
        foreach (string batch in _routing.EnabledBatches)
        {
            batches.Add(batch);
            batchesAlias.Add(batch);
        }

        return new JsonObject
        {
            ["version"] = HaloAgent.Engine.Abstractions.ContractCatalog.ContractVersion,
            ["queueBound"] = HaloAgent.Engine.FrameServer.QueueBound,
            ["journalCapacity"] = _channel.Ring.Capacity,
            ["routing"] = new JsonObject { ["enabledBatches"] = batches, ["defaultFrame"] = true, },
            ["batches"] = batchesAlias,
            ["bridgeVersion"] = HaloAgent.Bridge.Contracts.BridgeVersion.ProtocolVersion,
            ["contractVersion"] = HaloAgent.Engine.Abstractions.ContractCatalog.ContractVersion,
        };
    }

    private static string SuccessResponse(long id, JsonNode? result)
    {
        var document = new JsonObject
        {
            ["t"] = "res",
            ["id"] = id,
            ["ok"] = true,
            ["r"] = result ?? new JsonObject(),
        };
        return document.ToJsonString(HaloJson.Options);
    }

    private static string FailResponse(long id, string code, string message, string? hint = null)
    {
        var error = new JsonObject { ["code"] = code, ["msg"] = message, };
        if (!string.IsNullOrWhiteSpace(hint))
        {
            error["hint"] = hint;
        }
        var document = new JsonObject
        {
            ["t"] = "res",
            ["id"] = id,
            ["ok"] = false,
            ["e"] = error,
        };
        return document.ToJsonString(HaloJson.Options);
    }

    private static string SerializeResponse(ResponseFrame response)
    {
        var document = new JsonObject
        {
            ["t"] = "res",
            ["id"] = response.Id,
            ["ok"] = response.Ok,
        };
        if (response.Ok)
        {
            document["r"] = response.Result is { } result
                ? JsonNode.Parse(result.GetRawText())
                : new JsonObject();
        }
        else if (response.Error is { } error)
        {
            var body = new JsonObject { ["code"] = error.Code, ["msg"] = error.Message, };
            if (!string.IsNullOrWhiteSpace(error.Hint))
            {
                body["hint"] = error.Hint;
            }
            document["e"] = body;
        }
        else
        {
            document["e"] = new JsonObject { ["code"] = "INTERNAL", ["msg"] = "Request failed.", };
        }
        return document.ToJsonString(HaloJson.Options);
    }

    private static long ReadId(JsonElement root)
    {
        if (root.TryGetProperty("id", out var node))
        {
            if (node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out long value))
            {
                return value;
            }
        }
        return 0;
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String)
        {
            return node.GetString() ?? string.Empty;
        }
        return string.Empty;
    }
}
