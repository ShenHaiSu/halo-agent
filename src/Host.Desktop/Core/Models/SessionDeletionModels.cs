using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Host.Desktop.Core.Models;

/// <summary>会话树删除的最终状态；数据库提交后不再回退为失败。</summary>
[JsonConverter(typeof(SessionDeletionOutcomeJsonConverter))]
public enum SessionDeletionOutcome
{
    Deleted,
    Partial,
    Failed,
    NotFound,
}

/// <summary>会话树删除的存储阶段状态。</summary>
[JsonConverter(typeof(SessionDeletionStageJsonConverter))]
public enum SessionDeletionStage
{
    NotRequested,
    Completed,
    Failed,
}

internal sealed class SessionDeletionOutcomeJsonConverter : JsonConverter<SessionDeletionOutcome>
{
    public override SessionDeletionOutcome Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) =>
        Enum.TryParse(reader.GetString(), ignoreCase: true, out SessionDeletionOutcome value)
            ? value
            : throw new JsonException("Unknown session deletion outcome.");

    public override void Write(
        Utf8JsonWriter writer,
        SessionDeletionOutcome value,
        JsonSerializerOptions options
    ) => writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}

internal sealed class SessionDeletionStageJsonConverter : JsonConverter<SessionDeletionStage>
{
    public override SessionDeletionStage Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) =>
        Enum.TryParse(reader.GetString(), ignoreCase: true, out SessionDeletionStage value)
            ? value
            : throw new JsonException("Unknown session deletion stage.");

    public override void Write(
        Utf8JsonWriter writer,
        SessionDeletionStage value,
        JsonSerializerOptions options
    ) => writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}

/// <summary>待删除的会话节点；深度仅用于确定目录清理顺序。</summary>
public sealed class SessionDeletionNode
{
    public string SessionId { get; init; } = string.Empty;
    public int Depth { get; init; }
}

/// <summary>会话树删除结果，供桥接层与前端区分已提交和残留。</summary>
public sealed class SessionDeletionResult
{
    public SessionDeletionOutcome Outcome { get; init; }
    public string? ErrorCode { get; init; }
    public bool DestructiveCommitCompleted { get; init; }
    public string? BackupFileName { get; init; }
    public IReadOnlyList<string> DeletedSessionIds { get; init; } = new List<string>();
    public IReadOnlyList<string> ResidualSessionIds { get; init; } = new List<string>();
    public SessionDeletionStage Database { get; init; }
    public SessionDeletionStage Directories { get; init; }
    public SessionDeletionStage Index { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = new List<string>();
}

/// <summary>桥接专用 DTO：枚举显式映射为字符串，不依赖宿主 JSON 序列化器。</summary>
public sealed class SessionDeletionResultDto
{
    public string Outcome { get; init; } = "failed";
    public string? ErrorCode { get; init; }
    public bool DestructiveCommitCompleted { get; init; }
    public string? BackupFileName { get; init; }
    public IReadOnlyList<string> DeletedSessionIds { get; init; } = new List<string>();
    public IReadOnlyList<string> ResidualSessionIds { get; init; } = new List<string>();
    public string Database { get; init; } = "notRequested";
    public string Directories { get; init; } = "notRequested";
    public string Index { get; init; } = "notRequested";
    public IReadOnlyList<string> Warnings { get; init; } = new List<string>();

    public static SessionDeletionResultDto FromResult(SessionDeletionResult result) =>
        new()
        {
            Outcome = JsonNamingPolicy.CamelCase.ConvertName(result.Outcome.ToString()),
            ErrorCode = result.ErrorCode,
            DestructiveCommitCompleted = result.DestructiveCommitCompleted,
            BackupFileName = result.BackupFileName,
            DeletedSessionIds = result.DeletedSessionIds,
            ResidualSessionIds = result.ResidualSessionIds,
            Database = JsonNamingPolicy.CamelCase.ConvertName(result.Database.ToString()),
            Directories = JsonNamingPolicy.CamelCase.ConvertName(result.Directories.ToString()),
            Index = JsonNamingPolicy.CamelCase.ConvertName(result.Index.ToString()),
            Warnings = result.Warnings,
        };
}

/// <summary>仓储层阶段结果；只有数据库提交成功后才会执行目录与索引阶段。</summary>
public sealed class SessionDeletionStorageResult
{
    public bool DatabaseCommitted { get; init; }
    public string? ErrorCode { get; init; }
    public SessionDeletionStage Database { get; init; }
    public SessionDeletionStage Directories { get; init; }
    public SessionDeletionStage Index { get; init; }
    public IReadOnlyList<string> ResidualSessionIds { get; init; } = new List<string>();
    public IReadOnlyList<string> Warnings { get; init; } = new List<string>();
}
