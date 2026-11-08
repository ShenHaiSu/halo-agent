using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaloAgent.Engine.Abstractions;

/// <summary>Shared JSON settings: camel case names, tolerant reads.</summary>
public static class HaloJson
{
    public static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
}

/// <summary>Minimal engine log surface so the engine never takes a logging framework dependency.</summary>
public interface IEngineLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? error = null);
}

/// <summary>No-op log used by tests and the skeleton host.</summary>
public sealed class NullEngineLog : IEngineLog
{
    public static readonly NullEngineLog Instance = new();

    public void Info(string message) { }

    public void Warn(string message) { }

    public void Error(string message, Exception? error = null) { }
}
