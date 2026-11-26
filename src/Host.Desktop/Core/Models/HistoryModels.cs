using System;
using System.Collections.Generic;

namespace Host.Desktop.Core.Models;

/// <summary>轻量消息引用：仅含锚点投影所需字段，不携带正文、工具调用或图片字节。</summary>
public sealed record HistoryMessageRef(string Id, string Role, string? Kind);

/// <summary>历史缺失 user 的只读投影输入：仅含合成取消轮所需字段，不携带图片、工具调用或字节。</summary>
public sealed record HistoryUserTurn(
    string Id,
    string Content,
    string CreatedAtUtc,
    long MessageSeq
);

/// <summary>重放图片引用：lost 的图片只产生警告，不参与重新发送。</summary>
public sealed class HistoryReplayImageDto
{
    public string ImageId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Mime { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public long SizeBytes { get; init; }
    public string Sha16 { get; init; } = string.Empty;
    public bool Lost { get; init; }

    public static HistoryReplayImageDto FromReference(ChatImageRef reference, bool lost) =>
        new()
        {
            ImageId = reference.ImageId ?? string.Empty,
            FileName = reference.FileName ?? string.Empty,
            Mime = reference.Mime ?? string.Empty,
            Width = reference.Width,
            Height = reference.Height,
            SizeBytes = reference.SizeBytes,
            Sha16 = reference.Sha16 ?? string.Empty,
            Lost = lost
        };
}

/// <summary>历史能力位：旧后端缺失时由前端归一为全部不支持。</summary>
public sealed class HistoryCapabilities
{
    public bool HistoryAnchors { get; init; }
    public bool ReplayImages { get; init; }
    public bool AtomicHistoryMutex { get; init; }
    public bool StrictBranchRollback { get; init; }

    public static HistoryCapabilities Supported { get; } =
        new()
        {
            HistoryAnchors = true,
            ReplayImages = true,
            AtomicHistoryMutex = true,
            StrictBranchRollback = true
        };

    public static HistoryCapabilities Unsupported { get; } = new();
}

/// <summary>严格回滚的失败阶段，分别对应数据库分区、会话元数据、会话目录与会话索引。</summary>
public enum BranchRollbackStage
{
    Partition,
    Metadata,
    Directory,
    Index
}

/// <summary>单个回滚阶段的失败记录，用于向调用方暴露残留位置。</summary>
public sealed record HistoryRollbackFailure(BranchRollbackStage Stage, string Detail);

/// <summary>严格回滚结果：只有 Failures 为空才允许对外宣称“已回滚”。</summary>
public sealed class HistoryRollbackResult
{
    public bool PartitionDeleted { get; set; }
    public bool MetadataDeleted { get; set; }
    public bool DirectoryDeleted { get; set; }
    public bool IndexRemoved { get; set; }

    public List<HistoryRollbackFailure> Failures { get; } = new();

    public bool HasFailure => Failures.Count > 0;

    public string FailedStages =>
        Failures.Count == 0
            ? string.Empty
            : string.Join(",", Failures.ConvertAll(f => f.Stage.ToString()));
}

/// <summary>回滚失败异常：携带残留会话 ID 与各阶段验证结果，禁止被吞掉。</summary>
public sealed class BranchRollbackException : InvalidOperationException
{
    public string NewSessionId { get; }

    public HistoryRollbackResult Rollback { get; }

    public string FailedStages => Rollback.FailedStages;

    public BranchRollbackException(
        string newSessionId,
        HistoryRollbackResult rollback,
        Exception? innerException = null
    )
        : base("BRANCH_ROLLBACK_FAILED", innerException)
    {
        NewSessionId = newSessionId;
        Rollback = rollback;
    }
}

/// <summary>截断提交结果不确定：禁止前端继续发送，只提示刷新确认。</summary>
public sealed class TruncateCommitException : InvalidOperationException
{
    public TruncateCommitException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
