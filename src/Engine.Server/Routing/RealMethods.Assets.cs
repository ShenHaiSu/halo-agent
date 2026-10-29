using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Abstractions.Methods;
using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Storage;
using HaloAgent.Engine.Storage.Config;
using HaloAgent.Engine.Storage.Personas;
using HaloAgent.Engine.Storage.Skills;

namespace HaloAgent.Engine.Server.Routing;

public sealed partial class RealMethods
{
    // P10：state.setProvider 已删除，改调 settings.setActive（持久化+热切）。


    // ---------- asset.skills.list ----------
    public Task<object?> AssetSkillsListAsync(JsonElement? p, CancellationToken ct)
    {
        var set = SkillScanner.Scan(_session.Cwd, _dataDir);
        var items = set.Skills.Select(s => new SkillSummary
        {
            Name = s.Name,
            Description = s.Description,
            Source = s.Source switch
            {
                SkillSource.Builtin => "builtin",
                SkillSource.Project => "project",
                _ => "user",
            },
            DisableModelInvocation = s.DisableModelInvocation,
            HasEntry = s.Entry is not null,
        }).ToList();
        return Task.FromResult<object?>(new AssetSkillsListResult
        {
            Items = items,
            Warnings = set.Warnings,
        });
    }

    // ---------- asset.skills.get ----------
    public Task<object?> AssetSkillsGetAsync(JsonElement? p, CancellationToken ct)
    {
        var name = p?.TryGetProperty("name", out var nv) == true ? nv.GetString() : null;
        if (string.IsNullOrWhiteSpace(name) || !SkillScanner.IsValidName(name))
            throw RpcException.ParamsInvalid("技能名非法（≤40 字符，仅字母数字 _ -，不以 - 开头）");

        var set = SkillScanner.Scan(_session.Cwd, _dataDir);
        var skill = set.Skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        if (skill is null)
            throw new RpcException(RpcError.AssetNotFound, "技能不存在: " + name);

        return Task.FromResult<object?>(new AssetSkillsGetResult
        {
            Name = skill.Name,
            Body = skill.Body,
            Entry = skill.Entry,
            Source = skill.Source switch
            {
                SkillSource.Builtin => "builtin",
                SkillSource.Project => "project",
                _ => "user",
            },
        });
    }

    // ---------- asset.skills.save ----------
    public Task<object?> AssetSkillsSaveAsync(JsonElement? p, CancellationToken ct)
    {
        var name = p?.TryGetProperty("name", out var nv) == true ? nv.GetString() : null;
        var body = p?.TryGetProperty("body", out var bv) == true ? bv.GetString() : null;

        if (string.IsNullOrWhiteSpace(name) || !SkillScanner.IsValidName(name))
            throw RpcException.ParamsInvalid("技能名非法（≤40 字符，仅字母数字 _ -，不以 - 开头）");
        if (body is null)
            throw RpcException.ParamsInvalid("body 不能为空");

        var dir = Path.Combine(_dataDir, "skills", name);
        Directory.CreateDirectory(dir);

        string text;
        if (body.TrimStart().StartsWith("---"))
        {
            text = body;
        }
        else
        {
            // 保留已有 description，避免改 body 丢描述（D-3 红线）
            var existingDesc = TryReadSkillDescription(Path.Combine(dir, "SKILL.md"));
            text = "---\nname: " + name + "\ndescription: " + (existingDesc ?? "") + "\n---\n" + body;
        }

        var file = Path.Combine(dir, "SKILL.md");
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, text, Encoding.UTF8);
        if (File.Exists(file)) File.Delete(file);
        File.Move(tmp, file);

        return Task.FromResult<object?>(new AssetSkillsSaveResult { Ok = true });
    }

    // ---------- asset.skills.delete ----------
    public Task<object?> AssetSkillsDeleteAsync(JsonElement? p, CancellationToken ct)
    {
        var name = p?.TryGetProperty("name", out var nv) == true ? nv.GetString() : null;
        if (string.IsNullOrWhiteSpace(name) || !SkillScanner.IsValidName(name))
            throw RpcException.ParamsInvalid("技能名非法");

        var dir = Path.Combine(_dataDir, "skills", name);
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            throw new RpcException(RpcError.Internal, "删除技能失败：" + ex.Message);
        }

        return Task.FromResult<object?>(new AssetSkillsDeleteResult { Ok = true });
    }

    // ---------- asset.skills.run（明确降级） ----------
    public Task<object?> AssetSkillsRunAsync(JsonElement? p, CancellationToken ct)
    {
        throw new RpcException(RpcError.NotSupported,
            "技能执行尚未接通（P07 未纳入范围）",
            "请改用 /命令 方式或等待后续版本");
    }

    // ---------- asset.personas.list ----------
    public Task<object?> AssetPersonasListAsync(JsonElement? p, CancellationToken ct)
    {
        var names = PersonaStore.List(_dataDir);
        return Task.FromResult<object?>(new AssetPersonasListResult { Items = names });
    }

    // ---------- asset.personas.get ----------
    public Task<object?> AssetPersonasGetAsync(JsonElement? p, CancellationToken ct)
    {
        var name = p?.TryGetProperty("name", out var nv) == true ? nv.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
            throw RpcException.ParamsInvalid("name 不能为空");

        var body = PersonaStore.Get(_dataDir, name);
        if (body is null)
            throw new RpcException(RpcError.AssetNotFound, "人格不存在: " + name);

        return Task.FromResult<object?>(new AssetPersonasGetResult { Name = name, Body = body });
    }

    // ---------- asset.personas.save ----------
    public Task<object?> AssetPersonasSaveAsync(JsonElement? p, CancellationToken ct)
    {
        var name = p?.TryGetProperty("name", out var nv) == true ? nv.GetString() : null;
        var body = p?.TryGetProperty("body", out var bv) == true ? bv.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
            throw RpcException.ParamsInvalid("name 不能为空");
        if (body is null)
            throw RpcException.ParamsInvalid("body 不能为空");

        try { PersonaStore.Save(_dataDir, name, body); }
        catch (ArgumentException ex)
        {
            throw RpcException.ParamsInvalid("人格名非法：" + ex.Message);
        }

        return Task.FromResult<object?>(new AssetPersonasSaveResult { Ok = true });
    }

    // ---------- asset.memory.list ----------
    public Task<object?> AssetMemoryListAsync(JsonElement? p, CancellationToken ct)
    {
        var items = new List<MemorySummary>();
        foreach (var scope in new[] { "project", "global" })
        {
            var dir = ResolveMemoryDir(scope);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.md"))
            {
                ct.ThrowIfCancellationRequested();
                if (string.Equals(Path.GetFileName(f), "MEMORY.md", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    var text = File.ReadAllText(f, Encoding.UTF8);
                    var (fm, _) = ParseSimpleFrontmatter(text);
                    items.Add(new MemorySummary
                    {
                        Filename = Path.GetFileName(f),
                        Name = fm.GetValueOrDefault("name", Path.GetFileNameWithoutExtension(f)),
                        Description = fm.GetValueOrDefault("description", ""),
                        Mtype = fm.GetValueOrDefault("mtype", fm.GetValueOrDefault("type", "")),
                        Scope = scope,
                        UpdatedMs = new DateTimeOffset(File.GetLastWriteTimeUtc(f)).ToUnixTimeMilliseconds(),
                    });
                }
                catch { }
            }
        }
        items.Sort((a, b) => string.CompareOrdinal(a.Filename, b.Filename));
        return Task.FromResult<object?>(new AssetMemoryListResult { Items = items });
    }

    // ---------- asset.memory.get ----------
    public Task<object?> AssetMemoryGetAsync(JsonElement? p, CancellationToken ct)
    {
        var filename = p?.TryGetProperty("filename", out var fv) == true ? fv.GetString() : null;
        var scope = p?.TryGetProperty("scope", out var sv) == true ? sv.GetString() : "global";
        if (!IsSafeMemoryFile(filename))
            throw RpcException.ParamsInvalid("filename 非法");
        var normScope = NormalizeMemoryScope(scope);

        var dir = ResolveMemoryDir(normScope);
        var path = Path.Combine(dir, filename!);
        if (!File.Exists(path))
            throw new RpcException(RpcError.AssetNotFound, "记忆不存在: " + filename);

        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch (Exception ex)
        {
            throw new RpcException(RpcError.Internal, "读取记忆失败：" + ex.Message);
        }
        var (fm, body) = ParseSimpleFrontmatter(text);
        return Task.FromResult<object?>(new AssetMemoryGetResult
        {
            Name = fm.GetValueOrDefault("name", Path.GetFileNameWithoutExtension(filename!)),
            Description = fm.GetValueOrDefault("description", ""),
            Mtype = fm.GetValueOrDefault("mtype", fm.GetValueOrDefault("type", "")),
            Scope = normScope,
            Body = body,
        });
    }

    // ---------- asset.memory.save ----------
    public Task<object?> AssetMemorySaveAsync(JsonElement? p, CancellationToken ct)
    {
        var filename = p?.TryGetProperty("filename", out var fv) == true ? fv.GetString() : null;
        var name = p?.TryGetProperty("name", out var nv) == true ? nv.GetString() ?? "" : "";
        var description = p?.TryGetProperty("description", out var dv) == true ? dv.GetString() ?? "" : "";
        var mtype = p?.TryGetProperty("mtype", out var tv) == true ? tv.GetString() ?? "" : "";
        var scope = p?.TryGetProperty("scope", out var sv) == true ? sv.GetString() : "global";
        var body = p?.TryGetProperty("body", out var bv) == true ? bv.GetString() : null;
        if (body is null)
            throw RpcException.ParamsInvalid("body 不能为空");

        var normScope = NormalizeMemoryScope(scope);
        string finalName;
        if (string.IsNullOrWhiteSpace(filename))
        {
            var slug = SlugifyMemoryName(string.IsNullOrWhiteSpace(name) ? "memory" : name);
            finalName = slug + ".md";
            // 避免碰撞：若已存在则追加时间戳
            var dir0 = ResolveMemoryDir(normScope);
            if (File.Exists(Path.Combine(dir0, finalName)))
                finalName = $"{slug}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}.md";
        }
        else
        {
            if (!IsSafeMemoryFile(filename))
                throw RpcException.ParamsInvalid("filename 非法");
            finalName = filename;
        }

        var dir = ResolveMemoryDir(normScope);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, finalName);

        // 双保险：确认路径未逃出作用域目录
        var fullParent = Path.GetFullPath(Path.GetDirectoryName(path)!);
        if (!string.Equals(fullParent, Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
            throw RpcException.ParamsInvalid("filename 非法");

        var fileText = "---\nname: " + name + "\ndescription: " + description
            + "\nmtype: " + mtype + "\nscope: " + normScope + "\n---\n" + body;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, fileText, Encoding.UTF8);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);

        return Task.FromResult<object?>(new AssetMemorySaveResult { Ok = true, Filename = finalName });
    }

    // ---------- asset.memory.delete ----------
    public Task<object?> AssetMemoryDeleteAsync(JsonElement? p, CancellationToken ct)
    {
        var filename = p?.TryGetProperty("filename", out var fv) == true ? fv.GetString() : null;
        var scope = p?.TryGetProperty("scope", out var sv) == true ? sv.GetString() : "global";
        if (!IsSafeMemoryFile(filename))
            throw RpcException.ParamsInvalid("filename 非法");
        var normScope = NormalizeMemoryScope(scope);

        var path = Path.Combine(ResolveMemoryDir(normScope), filename!);
        try
        {
            if (!File.Exists(path))
                throw new RpcException(RpcError.AssetNotFound, "记忆不存在: " + filename);
            File.Delete(path);
        }
        catch (RpcException) { throw; }
        catch (Exception ex)
        {
            throw new RpcException(RpcError.Internal, "删除记忆失败：" + ex.Message);
        }

        return Task.FromResult<object?>(new AssetMemoryDeleteResult { Ok = true });
    }

    // ---------- asset.themes.list ----------
    public Task<object?> AssetThemesListAsync(JsonElement? p, CancellationToken ct)
    {
        var items = new List<ThemeSummary>();
        var dir = Path.Combine(_dataDir, "themes");

        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
            {
                ct.ThrowIfCancellationRequested();
                var id = Path.GetFileNameWithoutExtension(f);
                var (name, basedOn, ok) = ReadThemeMeta(f);
                if (!ok) continue;
                items.Add(new ThemeSummary
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(name) ? id : name,
                    BasedOn = basedOn,
                    Source = "user",
                });
            }
        }

        items.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return Task.FromResult<object?>(new AssetThemesListResult { Items = items });
    }

    // ---------- asset.themes.get ----------
    public Task<object?> AssetThemesGetAsync(JsonElement? p, CancellationToken ct)
    {
        var id = p?.TryGetProperty("id", out var el) == true ? el.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || !IsSafeThemeId(id))
            throw RpcException.ParamsInvalid("id 非法");

        var path = Path.Combine(_dataDir, "themes", id + ".json");
        if (!File.Exists(path))
            throw new RpcException(RpcError.AssetNotFound, "主题不存在: " + id);

        var model = ReadThemeFile(path)
            ?? throw new RpcException(RpcError.Internal, "主题文件解析失败: " + id);

        return Task.FromResult<object?>(new AssetThemesGetResult
        {
            Id = id,
            Name = model.Name,
            BasedOn = model.BasedOn,
            Tokens = model.Tokens,
        });
    }

    // ---------- 私有辅助 ----------
    private static bool IsSafeThemeId(string id)
    {
        if (id.Length > 64) return false;
        if (id is "." or "..") return false;
        foreach (var c in id)
        {
            if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_')) return false;
        }
        return true;
    }

    private static (string name, string? basedOn, bool ok) ReadThemeMeta(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var basedOn = root.TryGetProperty("basedOn", out var b) ? b.GetString() : null;
            return (name, basedOn, true);
        }
        catch
        {
            return ("", null, false);
        }
    }

    private static ThemeFileModel? ReadThemeFile(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ThemeFileModel>(
                File.ReadAllText(path), StorageJson.Options);
        }
        catch
        {
            return null;
        }
    }

    private sealed class ThemeFileModel
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("basedOn")]
        public string? BasedOn { get; set; }

        [JsonPropertyName("tokens")]
        public Dictionary<string, string> Tokens { get; set; } = new();
    }

    private static string? TryReadSkillDescription(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            if (!text.StartsWith("---")) return null;
            var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end < 0) return null;
            var header = text[3..end];
            foreach (var line in header.Split('\n'))
            {
                var l = line.Trim();
                if (l.StartsWith("description:", StringComparison.Ordinal))
                    return l[12..].Trim().Trim('"', '\'');
            }
            return null;
        }
        catch { return null; }
    }

    private string ResolveMemoryDir(string scope) =>
        scope == "project" ? Paths.ProjectMemoryDir(_session.Cwd) : Path.Combine(_dataDir, "memories");

    private static string NormalizeMemoryScope(string? scope)
    {
        var s = (scope ?? "global").Trim().ToLowerInvariant();
        return s switch
        {
            "project" or "repo" => "project",
            "global" or "user" or "" => "global",
            _ => throw RpcException.ParamsInvalid("scope 只能是 global/project（收到 " + scope + "）"),
        };
    }

    private static bool IsSafeMemoryFile(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename.Length > 120) return false;
        if (filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (filename is "." or "..") return false;
        if (filename.Contains('/') || filename.Contains('\\')) return false;
        if (!filename.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string SlugifyMemoryName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            else if (c == ' ' || c == '.') sb.Append('_');
        }
        var s = sb.ToString().Trim('_');
        if (s.Length == 0) s = "memory";
        if (s.Length > 60) s = s[..60];
        return s;
    }

    private static (Dictionary<string, string> fm, string body) ParseSimpleFrontmatter(string text)
    {
        var fm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!text.StartsWith("---")) return (fm, text.Trim());
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return (fm, text.Trim());
        var header = text[3..end];
        var body = text[(end + 4)..].Trim();
        foreach (var line in header.Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var val = line[(idx + 1)..].Trim().Trim('"', '\'');
            if (key.Length > 0) fm[key] = val;
        }
        return (fm, body);
    }
}
