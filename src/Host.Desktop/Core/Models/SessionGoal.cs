namespace Host.Desktop.Core.Models;

/// <summary>need16 Goal：主会话级目标边车模型（sessions/&lt;ws&gt;/&lt;sess&gt;/goal.json）。</summary>
public sealed class GoalMilestone
{
    public string Id { get; set; } = "ms_" + Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = string.Empty;
    public bool Done { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class SessionGoal
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<GoalMilestone> Milestones { get; set; } = new();
    public int Percent { get; set; }
    public string Note { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string UpdatedBy { get; set; } = "main"; // main|user|sub-read

    public bool HasContent => !string.IsNullOrWhiteSpace(Title)
        || !string.IsNullOrWhiteSpace(Description)
        || Milestones.Count > 0;

    public int DoneCount => Milestones.Count(m => m.Done);
}

/// <summary>goal.json 损坏标记异常（内含路径，供 Bridge 横幅位使用）。</summary>
public sealed class GoalCorruptException : Exception
{
    public string FilePath { get; }
    public GoalCorruptException(string path, string message) : base(message) { FilePath = path ?? string.Empty; }
}
