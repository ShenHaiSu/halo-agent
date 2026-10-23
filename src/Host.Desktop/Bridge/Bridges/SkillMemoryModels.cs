using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Services;

namespace Host.Desktop.Bridge;

/// <summary>need10-02：Skill/Memory 只读管理 Bridge DTO。</summary>
public class SkillMemoryListItemDto
{
    public string Name { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Pin { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int Priority { get; set; }
    public int Version { get; set; }
    public int EstTokens { get; set; }
    public int Chars { get; set; }
    public string Hash { get; set; } = string.Empty;
    public bool HasAssets { get; set; }
    public string UpdatedAtUtc { get; set; } = string.Empty;
    public string DirPath { get; set; } = string.Empty;
}

/// <summary>need10-02：详情 DTO（正文截断 8000→前 800 字）。</summary>
public sealed class SkillMemoryDetailDto : SkillMemoryListItemDto
{
    public string BodyPreview { get; set; } = string.Empty;
    public bool Truncated { get; set; }
    public int BodyTotalChars { get; set; }
}

/// <summary>need10-02：写操作结果。</summary>
public sealed class SkillMemoryOpResult
{
    public bool Ok { get; set; }
    public string? BakFileName { get; set; }
    public string Notice { get; set; } = string.Empty;
}
