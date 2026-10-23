#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Services;

/// <summary>need09-01：Skill/Memory 域（Global 随数据根走 / Project 随工作区 .haloagent/ 走）。</summary>
public enum SkillMemoryScope
{
    Global,
    Project
}

/// <summary>need09-01：条目类别。</summary>
public enum SkillMemoryKind
{
    Skill,
    Memory
}

/// <summary>need09-01：Skill/Memory 校验失败（非法名/超限/遍历逃逸等）。Message 恒含 Code 前缀。</summary>
public sealed class SkillMemoryValidationException : Exception
{
    public string Code { get; }

    public SkillMemoryValidationException(string code, string message)
        : base($"[{code}] {message}")
    {
        Code = code;
    }
}

/// <summary>need09-01：单条 Skill/Memory 快照。Scope 取值 "global"|"project"。</summary>
public sealed record SkillMemoryEntry(
    string Name,
    string Scope,
    string Title,
    string Summary,
    string BodyMarkdown,
    string Pin,
    bool Enabled,
    int Priority,
    int Version,
    int EstTokens,
    string Hash,
    bool HasAssets,
    string DirPath,
    DateTime UpdatedAtUtc
)
{
    public SkillMemoryKind Kind { get; init; } = SkillMemoryKind.Skill;
}
