namespace Host.Desktop.Core.Models;

/// <summary>时间轴块展示类型：四值封闭，后端终裁，前端只做展示。</summary>
public static class TimelineBlockKind
{
    public const string Reasoning = "reasoning";
    public const string Text = "text";
    public const string ToolCall = "tool_call";
    public const string ToolResult = "tool_result";
}

/// <summary>时间轴块落盘标签：pending 只活内存，落盘前终裁为其余两值。</summary>
public static class TimelineBlockTag
{
    public const string Pending = "pending";
    public const string Process = "process";
    public const string Final = "final";
}

/// <summary>时间轴标签变更原因，与终裁表对齐，前端只按此表消费。</summary>
public static class TimelineTagReason
{
    public const string Demote = "demote";
    public const string Finalize = "finalize";
    public const string Flip = "flip";
}

/// <summary>轮次结束徽章，直接落盘，重进照显。</summary>
public static class TimelineTurnStatus
{
    public const string Completed = "completed";
    public const string Truncated = "truncated";
    public const string Cancelled = "cancelled";
    public const string Error = "error";
}

/// <summary>时间轴块：后端切分的最小展示单元，序号全局单调。</summary>
public sealed class TimelineBlock
{
    public string BlockId { get; set; } = string.Empty;
    public string TurnId { get; set; } = string.Empty;
    public int Iteration { get; set; }
    public long Sequence { get; set; }
    public string Kind { get; set; } = TimelineBlockKind.Text;
    public string Tag { get; set; } = TimelineBlockTag.Process;
    public string? CallId { get; set; }
    public string? FunctionName { get; set; }

    /// <summary>逐工具成败位：仅 tool_result 行有效；true=成功，false=失败，null=未知（老数据）。</summary>
    public bool? Success { get; set; }
    public int CharCount { get; set; }
    public string Preview { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ContentRef { get; set; }
    public bool Truncated { get; set; }
}

/// <summary>轮次摘要：目录与盒头计数的唯一真相源，直接映射 turns 表。</summary>
public sealed class TurnSummary
{
    public string TurnId { get; set; } = string.Empty;
    public long TurnSeq { get; set; }
    public int IterEnd { get; set; }
    public string UserPreview { get; set; } = string.Empty;
    public string? FinalBlockId { get; set; }
    public string FinalPreview { get; set; } = string.Empty;
    public int Blocks { get; set; }
    public int Tools { get; set; }
    public int Failed { get; set; }
    public long DurationMs { get; set; }
    public int Chars { get; set; }
    public string Status { get; set; } = TimelineTurnStatus.Completed;
    public string LastRequestId { get; set; } = string.Empty;
    public string CreatedAtUtc { get; set; } = string.Empty;
    public string UpdatedAtUtc { get; set; } = string.Empty;
    public string AnchorKey { get; set; } = string.Empty;
}

/// <summary>块内容点查结果：小块直接给全文，大块给引用再读文件。</summary>
public sealed class TimelineBlockContent
{
    public string BlockId { get; set; } = string.Empty;
    public string? CallId { get; set; }
    public string? FunctionName { get; set; }
    public long Seq { get; set; }
    public string Kind { get; set; } = TimelineBlockKind.Text;
    public string Tag { get; set; } = TimelineBlockTag.Process;
    public string Markdown { get; set; } = string.Empty;
    public string Preview { get; set; } = string.Empty;
    public bool Truncated { get; set; }

    /// <summary>逐工具成败位：仅 tool_result 行透出；null=未知（老数据），与 false 严格区分。</summary>
    public bool? Success { get; set; }
    public string? ArgsJson { get; set; }
    public string? ResultText { get; set; }
    public string? ResultRef { get; set; }
}

/// <summary>标签变更事件载荷：乐观镜像收敛到后端终裁的桥梁。</summary>
public sealed class TimelineTagUpdated
{
    public string BlockId { get; set; } = string.Empty;
    public string Tag { get; set; } = TimelineBlockTag.Process;
    public string Reason { get; set; } = TimelineTagReason.Demote;
}

/// <summary>工具结果兼容查：按 turn + call 定位，主路径优先块主键点查。</summary>
public sealed class TimelineToolResult
{
    public string ArgsJson { get; set; } = string.Empty;
    public string ResultText { get; set; } = string.Empty;
    public string? FunctionName { get; set; }
    public string? ResultRef { get; set; }

    /// <summary>逐工具成败位：null=未知（老数据），与 false 严格区分。</summary>
    public bool? Success { get; set; }
}

/// <summary>批量块内容响应：成功项 + 缺失占位。</summary>
public sealed class TimelineBlockContentsResponse
{
    public Dictionary<string, TimelineBlockContent> Items { get; set; } =
        new(StringComparer.Ordinal);
    public List<string> Missing { get; set; } = new();
}
