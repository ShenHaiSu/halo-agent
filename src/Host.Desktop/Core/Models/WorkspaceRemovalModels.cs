using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Host.Desktop.Core.Models;

/// <summary>工作区移除模式：解绑保留物理数据，彻底删除清理两个明确根目录。</summary>
[JsonConverter(typeof(WorkspaceRemovalModeJsonConverter))]
public enum WorkspaceRemovalMode
{
    Unbind,
    Purge,
}

/// <summary>工作区移除总结果；已解绑的阶段不再回退为失败。</summary>
[JsonConverter(typeof(WorkspaceRemovalOutcomeJsonConverter))]
public enum WorkspaceRemovalOutcome
{
    Completed,
    CompletedWithWarnings,
    Failed,
    NotFound,
}

/// <summary>单个物理资源的阶段状态。</summary>
[JsonConverter(typeof(WorkspaceResourceStatusJsonConverter))]
public enum WorkspaceResourceStatus
{
    NotRequested,
    Deleted,
    Retained,
    Failed,
}

internal sealed class WorkspaceRemovalModeJsonConverter : JsonConverter<WorkspaceRemovalMode>
{
    public override WorkspaceRemovalMode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) =>
        Enum.TryParse(reader.GetString(), ignoreCase: true, out WorkspaceRemovalMode value)
            ? value
            : throw new JsonException("Unknown workspace removal mode.");

    public override void Write(
        Utf8JsonWriter writer,
        WorkspaceRemovalMode value,
        JsonSerializerOptions options
    ) => writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}

internal sealed class WorkspaceRemovalOutcomeJsonConverter : JsonConverter<WorkspaceRemovalOutcome>
{
    public override WorkspaceRemovalOutcome Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) =>
        Enum.TryParse(reader.GetString(), ignoreCase: true, out WorkspaceRemovalOutcome value)
            ? value
            : throw new JsonException("Unknown workspace removal outcome.");

    public override void Write(
        Utf8JsonWriter writer,
        WorkspaceRemovalOutcome value,
        JsonSerializerOptions options
    ) => writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}

internal sealed class WorkspaceResourceStatusJsonConverter : JsonConverter<WorkspaceResourceStatus>
{
    public override WorkspaceResourceStatus Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) =>
        Enum.TryParse(reader.GetString(), ignoreCase: true, out WorkspaceResourceStatus value)
            ? value
            : throw new JsonException("Unknown workspace resource status.");

    public override void Write(
        Utf8JsonWriter writer,
        WorkspaceResourceStatus value,
        JsonSerializerOptions options
    ) => writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}

/// <summary>桥接层的 mode 字符串严格归一：只接受 unbind / purge，大小写不敏感。</summary>
public static class WorkspaceRemovalModeParser
{
    public static bool TryParse(string? value, out WorkspaceRemovalMode mode)
    {
        mode = WorkspaceRemovalMode.Unbind;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        switch (value.Trim().ToLowerInvariant())
        {
            case "unbind":
                mode = WorkspaceRemovalMode.Unbind;
                return true;
            case "purge":
                mode = WorkspaceRemovalMode.Purge;
                return true;
            default:
                return false;
        }
    }

    public static string ToWire(WorkspaceRemovalMode mode) =>
        JsonNamingPolicy.CamelCase.ConvertName(mode.ToString());
}

/// <summary>项目域检查原因码；不承载正文等敏感信息。</summary>
public static class ProjectDomainReasons
{
    public const string NotFound = "NOT_FOUND";
    public const string Empty = "EMPTY";
    public const string ValidEntries = "VALID_ENTRIES";
    public const string UnknownContent = "UNKNOWN_CONTENT";
    public const string ReparsePoint = "REPARSE_POINT";
    public const string InspectionFailed = "INSPECTION_FAILED";
    public const string PathTraversal = "PATH_TRAVERSAL";
}

/// <summary>.haloagent 物理检查结果；索引条目数只作提示，不作为删除依据。</summary>
public sealed record ProjectDomainInspection(
    bool Exists,
    bool SafeEmpty,
    bool HasValidEntries,
    bool HasUnknownContent,
    bool HasReparsePoint,
    string? Reason
);

/// <summary>工作区移除的分阶段结果，供桥接层映射为稳定的前端语义。</summary>
public sealed class WorkspaceRemovalResult
{
    public WorkspaceRemovalOutcome Outcome { get; init; }
    public WorkspaceRemovalMode Mode { get; init; }
    public string? ErrorCode { get; init; }
    public bool WorkspaceUnbound { get; init; }
    public WorkspaceResourceStatus SessionData { get; init; }
    public WorkspaceResourceStatus HaloAgent { get; init; }
    public string? HaloAgentReason { get; init; }
    public IReadOnlyList<string> FailedPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>桥接专用 DTO：枚举显式映射为字符串，不依赖宿主 JSON 序列化器。</summary>
public sealed class WorkspaceRemovalResultDto
{
    public string Outcome { get; init; } = "failed";
    public string Mode { get; init; } = "unbind";
    public string? ErrorCode { get; init; }
    public bool WorkspaceUnbound { get; init; }
    public string SessionData { get; init; } = "notRequested";
    public string HaloAgent { get; init; } = "notRequested";
    public string? HaloAgentReason { get; init; }
    public string[] FailedPaths { get; init; } = Array.Empty<string>();
    public string[] Warnings { get; init; } = Array.Empty<string>();

    public static WorkspaceRemovalResultDto FromResult(WorkspaceRemovalResult result) =>
        new()
        {
            Outcome = JsonNamingPolicy.CamelCase.ConvertName(result.Outcome.ToString()),
            Mode = WorkspaceRemovalModeParser.ToWire(result.Mode),
            ErrorCode = result.ErrorCode,
            WorkspaceUnbound = result.WorkspaceUnbound,
            SessionData = JsonNamingPolicy.CamelCase.ConvertName(result.SessionData.ToString()),
            HaloAgent = JsonNamingPolicy.CamelCase.ConvertName(result.HaloAgent.ToString()),
            HaloAgentReason = result.HaloAgentReason,
            FailedPaths = ToArray(result.FailedPaths),
            Warnings = ToArray(result.Warnings),
        };

    private static string[] ToArray(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return Array.Empty<string>();
        var copy = new string[values.Count];
        for (int i = 0; i < values.Count; i++)
            copy[i] = values[i];
        return copy;
    }
}
