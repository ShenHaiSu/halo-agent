using HaloAgent.Engine.Abstractions.Types;

namespace HaloAgent.Engine.Abstractions.Methods;

// ============================ asset.skills.list ============================

/// <summary>asset.skills.list 结果。</summary>
public sealed record AssetSkillsListResult
{
    /// <summary>技能摘要列表。</summary>
    public IReadOnlyList<SkillSummary> Items { get; init; } = Array.Empty<SkillSummary>();
    /// <summary>警告。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

// ============================ asset.skills.get ============================

/// <summary>asset.skills.get 参数。</summary>
public sealed record AssetSkillsGetParams
{
    /// <summary>技能名。</summary>
    public string Name { get; init; } = "";
}

/// <summary>asset.skills.get 结果。</summary>
public sealed record AssetSkillsGetResult
{
    /// <summary>技能名。</summary>
    public string Name { get; init; } = "";
    /// <summary>正文。</summary>
    public string Body { get; init; } = "";
    /// <summary>入口文件（可为空）。</summary>
    public string? Entry { get; init; }
    /// <summary>来源。</summary>
    public string Source { get; init; } = "";
}

// ============================ asset.skills.save ============================

/// <summary>asset.skills.save 参数。</summary>
public sealed record AssetSkillsSaveParams
{
    /// <summary>技能名。</summary>
    public string Name { get; init; } = "";
    /// <summary>正文。</summary>
    public string Body { get; init; } = "";
}

/// <summary>asset.skills.save 结果。</summary>
public sealed record AssetSkillsSaveResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }
}

// ============================ asset.skills.delete ============================

/// <summary>asset.skills.delete 参数。</summary>
public sealed record AssetSkillsDeleteParams
{
    /// <summary>技能名。</summary>
    public string Name { get; init; } = "";
}

/// <summary>asset.skills.delete 结果。</summary>
public sealed record AssetSkillsDeleteResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }
}

// ============================ asset.skills.run ============================

/// <summary>asset.skills.run 参数。</summary>
public sealed record AssetSkillsRunParams
{
    /// <summary>技能名。</summary>
    public string Name { get; init; } = "";
    /// <summary>参数（JSON 字符串，可为空）。</summary>
    public string? Args { get; init; }
}

/// <summary>asset.skills.run 结果。</summary>
public sealed record AssetSkillsRunResult
{
    /// <summary>是否已接受执行。</summary>
    public bool Accepted { get; init; }
}

// ============================ asset.personas.list ============================

/// <summary>asset.personas.list 结果。</summary>
public sealed record AssetPersonasListResult
{
    /// <summary>人格名列表。</summary>
    public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();
}

// ============================ asset.personas.get ============================

/// <summary>asset.personas.get 参数。</summary>
public sealed record AssetPersonasGetParams
{
    /// <summary>人格名。</summary>
    public string Name { get; init; } = "";
}

/// <summary>asset.personas.get 结果。</summary>
public sealed record AssetPersonasGetResult
{
    /// <summary>人格名。</summary>
    public string Name { get; init; } = "";
    /// <summary>正文。</summary>
    public string Body { get; init; } = "";
}

// ============================ asset.personas.save ============================

/// <summary>asset.personas.save 参数。</summary>
public sealed record AssetPersonasSaveParams
{
    /// <summary>人格名。</summary>
    public string Name { get; init; } = "";
    /// <summary>正文。</summary>
    public string Body { get; init; } = "";
}

/// <summary>asset.personas.save 结果。</summary>
public sealed record AssetPersonasSaveResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }
}

// ============================ asset.memory.list ============================

/// <summary>asset.memory.list 结果。</summary>
public sealed record AssetMemoryListResult
{
    /// <summary>记忆摘要列表。</summary>
    public IReadOnlyList<MemorySummary> Items { get; init; } = Array.Empty<MemorySummary>();
}

// ============================ asset.memory.get ============================

/// <summary>asset.memory.get 参数。</summary>
public sealed record AssetMemoryGetParams
{
    /// <summary>文件名。</summary>
    public string Filename { get; init; } = "";
    /// <summary>作用域：“global” | “project”。</summary>
    public string Scope { get; init; } = "";
}

/// <summary>asset.memory.get 结果。</summary>
public sealed record AssetMemoryGetResult
{
    /// <summary>记忆名。</summary>
    public string Name { get; init; } = "";
    /// <summary>描述。</summary>
    public string Description { get; init; } = "";
    /// <summary>记忆类型。</summary>
    public string Mtype { get; init; } = "";
    /// <summary>作用域。</summary>
    public string Scope { get; init; } = "";
    /// <summary>正文。</summary>
    public string Body { get; init; } = "";
}

// ============================ asset.memory.save ============================

/// <summary>asset.memory.save 参数。</summary>
public sealed record AssetMemorySaveParams
{
    /// <summary>文件名（可为空：新建时由引擎生成）。</summary>
    public string? Filename { get; init; }
    /// <summary>记忆名。</summary>
    public string Name { get; init; } = "";
    /// <summary>描述。</summary>
    public string Description { get; init; } = "";
    /// <summary>记忆类型。</summary>
    public string Mtype { get; init; } = "";
    /// <summary>作用域：“global” | “project”。</summary>
    public string Scope { get; init; } = "";
    /// <summary>正文。</summary>
    public string Body { get; init; } = "";
}

/// <summary>asset.memory.save 结果。</summary>
public sealed record AssetMemorySaveResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }
    /// <summary>生效后的文件名。</summary>
    public string Filename { get; init; } = "";
}

// ============================ asset.memory.delete ============================

/// <summary>asset.memory.delete 参数。</summary>
public sealed record AssetMemoryDeleteParams
{
    /// <summary>文件名。</summary>
    public string Filename { get; init; } = "";
    /// <summary>作用域：“global” | “project”。</summary>
    public string Scope { get; init; } = "";
}

/// <summary>asset.memory.delete 结果。</summary>
public sealed record AssetMemoryDeleteResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }
}
// ============================ asset.themes.list ============================

/// <summary>asset.themes.list 结果。</summary>
public sealed record AssetThemesListResult
{
    /// <summary>主题摘要列表（只含元信息，不含 tokens）。</summary>
    public IReadOnlyList<ThemeSummary> Items { get; init; } = Array.Empty<ThemeSummary>();
}

/// <summary>主题摘要。</summary>
public sealed record ThemeSummary
{
    /// <summary>主题 id（= 文件名去扩展名）。</summary>
    public string Id { get; init; } = "";
    /// <summary>显示名。</summary>
    public string Name { get; init; } = "";
    /// <summary>基于哪个内置主题（可为空）。</summary>
    public string? BasedOn { get; init; }
    /// <summary>来源：“builtin” | “user”。</summary>
    public string Source { get; init; } = "user";
}

// ============================ asset.themes.get ============================

/// <summary>asset.themes.get 参数。</summary>
public sealed record AssetThemesGetParams
{
    /// <summary>主题 id。</summary>
    public string Id { get; init; } = "";
}

/// <summary>asset.themes.get 结果。</summary>
public sealed record AssetThemesGetResult
{
    /// <summary>主题 id。</summary>
    public string Id { get; init; } = "";
    /// <summary>显示名。</summary>
    public string Name { get; init; } = "";
    /// <summary>基于哪个内置主题（可为空）。</summary>
    public string? BasedOn { get; init; }
    /// <summary>token 集合（变量名 → CSS 值）。</summary>
    public IReadOnlyDictionary<string, string> Tokens { get; init; }
        = new Dictionary<string, string>();
}
