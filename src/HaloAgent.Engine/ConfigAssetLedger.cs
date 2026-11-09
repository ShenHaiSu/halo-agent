using System.Text.Json;

namespace HaloAgent.Engine;

/// <summary>
/// File-backed truth for configuration and asset reads behind the frame boundary.
/// Covers provider documents, network settings, behavior settings (including the
/// default-frame switch which defaults to frame), read-only personas, skill
/// index entries and memory entries. Secrets are never persisted as plaintext:
/// well-known plaintext key fields are stripped before the atomic rewrite, so
/// only ciphertext passthrough values reach disk with directory ACLs as the
/// cross-platform floor (Windows DPAPI user scope stays on the desktop chain).
/// Skill and memory writes stay on the desktop chain by design; this ledger
/// intentionally exposes no write path for them.
/// </summary>
public sealed class ConfigAssetLedger
{
    public const string FileName = "config-assets.json";

    private readonly object _gate = new();
    private string _providersJson = "{}";
    private string _activeProviderId = string.Empty;
    private string _activeModelId = string.Empty;
    private string _networkJson = "{}";
    private string _behaviorJson = "{\"frameDefault\":true}";
    private bool _frameDefault = true;
    private readonly List<LedgerEntry> _skills = new();
    private readonly List<LedgerEntry> _memory = new();
    private readonly List<LedgerEntry> _personas =
        new()
        {
            new LedgerEntry("default", "Default", "Default assistant persona", "global"),
            new LedgerEntry("concise", "Concise", "Short answers first", "global"),
        };

    public bool FrameDefault
    {
        get
        {
            lock (_gate)
            {
                return _frameDefault;
            }
        }
    }

    public string SaveProviders(JsonElement payload)
    {
        string raw = ExtractDocument(payload, "providers", "config");
        if (string.IsNullOrWhiteSpace(raw) || raw == "{}")
        {
            throw new LedgerValidationException("providers document is required.");
        }
        string scrubbed = ScrubSecrets(raw);
        lock (_gate)
        {
            _providersJson = scrubbed;
        }
        return scrubbed;
    }

    public string ProvidersDocument()
    {
        lock (_gate)
        {
            return _providersJson;
        }
    }

    public (string ProviderId, string ModelId) SaveActive(JsonElement payload)
    {
        string providerId = ReadText(payload, "providerId");
        string modelId = ReadText(payload, "modelId");
        if (string.IsNullOrWhiteSpace(providerId) && string.IsNullOrWhiteSpace(modelId))
        {
            throw new LedgerValidationException("providerId or modelId is required.");
        }
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(providerId))
            {
                _activeProviderId = providerId.Trim();
            }
            if (!string.IsNullOrWhiteSpace(modelId))
            {
                _activeModelId = modelId.Trim();
            }
            return (_activeProviderId, _activeModelId);
        }
    }

    public (string ProviderId, string ModelId) ActiveSelection()
    {
        lock (_gate)
        {
            return (_activeProviderId, _activeModelId);
        }
    }

    public string SaveNetwork(JsonElement payload)
    {
        string raw = ExtractDocument(payload, "network", "settings");
        if (string.IsNullOrWhiteSpace(raw) || raw == "{}")
        {
            throw new LedgerValidationException("network settings are required.");
        }
        string scrubbed = ScrubSecrets(raw);
        lock (_gate)
        {
            _networkJson = scrubbed;
        }
        return scrubbed;
    }

    public string NetworkDocument()
    {
        lock (_gate)
        {
            return _networkJson;
        }
    }

    public string SaveBehavior(JsonElement payload)
    {
        string raw = ExtractDocument(payload, "behavior", "settings");
        if (string.IsNullOrWhiteSpace(raw) || raw == "{}")
        {
            throw new LedgerValidationException("behavior settings are required.");
        }
        bool frameDefault = ReadFrameDefault(raw);
        lock (_gate)
        {
            _behaviorJson = raw;
            _frameDefault = frameDefault;
        }
        return raw;
    }

    public string BehaviorDocument()
    {
        lock (_gate)
        {
            return _behaviorJson;
        }
    }

    public IReadOnlyList<LedgerEntry> ListPersonas()
    {
        lock (_gate)
        {
            return _personas.ToList();
        }
    }

    public LedgerEntry? GetPersona(string name)
    {
        lock (_gate)
        {
            return _personas.FirstOrDefault(e =>
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)
            );
        }
    }

    public IReadOnlyList<LedgerEntry> ListSkills()
    {
        lock (_gate)
        {
            return _skills.ToList();
        }
    }

    public LedgerEntry? GetSkill(string name)
    {
        lock (_gate)
        {
            return _skills.FirstOrDefault(e =>
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)
            );
        }
    }

    public IReadOnlyList<LedgerEntry> ListMemory()
    {
        lock (_gate)
        {
            return _memory.ToList();
        }
    }

    public LedgerEntry? GetMemory(string name)
    {
        lock (_gate)
        {
            return _memory.FirstOrDefault(e =>
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)
            );
        }
    }

    public void SeedSkills(IEnumerable<LedgerEntry> entries)
    {
        lock (_gate)
        {
            _skills.Clear();
            _skills.AddRange(entries);
        }
    }

    public void SeedMemory(IEnumerable<LedgerEntry> entries)
    {
        lock (_gate)
        {
            _memory.Clear();
            _memory.AddRange(entries);
        }
    }

    public void Save(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        string snapshot;
        lock (_gate)
        {
            snapshot = JsonSerializer.Serialize(
                new PersistedShape
                {
                    ProvidersJson = _providersJson,
                    ActiveProviderId = _activeProviderId,
                    ActiveModelId = _activeModelId,
                    NetworkJson = _networkJson,
                    BehaviorJson = _behaviorJson,
                    FrameDefault = _frameDefault,
                    Skills = _skills.ToList(),
                    Memory = _memory.ToList(),
                }
            );
        }
        try
        {
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, FileName);
            string staging = target + ".tmp";
            File.WriteAllText(staging, snapshot);
            File.Copy(staging, target, overwrite: true);
            File.Delete(staging);
        }
        catch { }
    }

    public void Load(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        string target = Path.Combine(directory, FileName);
        if (!File.Exists(target))
        {
            return;
        }
        try
        {
            string raw = File.ReadAllText(target);
            var shape = JsonSerializer.Deserialize<PersistedShape>(raw);
            if (shape is null)
            {
                return;
            }
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(shape.ProvidersJson))
                {
                    _providersJson = shape.ProvidersJson;
                }
                _activeProviderId = shape.ActiveProviderId ?? string.Empty;
                _activeModelId = shape.ActiveModelId ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(shape.NetworkJson))
                {
                    _networkJson = shape.NetworkJson;
                }
                if (!string.IsNullOrWhiteSpace(shape.BehaviorJson))
                {
                    _behaviorJson = shape.BehaviorJson;
                    _frameDefault = shape.FrameDefault;
                }
                _skills.Clear();
                if (shape.Skills is not null)
                {
                    _skills.AddRange(shape.Skills);
                }
                _memory.Clear();
                if (shape.Memory is not null)
                {
                    _memory.AddRange(shape.Memory);
                }
            }
        }
        catch { }
    }

    private static string ExtractDocument(JsonElement payload, params string[] keys)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return payload.ValueKind == JsonValueKind.Undefined ? "{}" : payload.GetRawText();
        }
        foreach (string key in keys)
        {
            if (payload.TryGetProperty(key, out var node))
            {
                return node.GetRawText();
            }
        }
        foreach (string key in new[] { "document", "value", "json" })
        {
            if (payload.TryGetProperty(key, out var node))
            {
                return node.GetRawText();
            }
        }
        return payload.GetRawText();
    }

    private static string ReadText(JsonElement payload, string name)
    {
        if (
            payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var node)
            && node.ValueKind == JsonValueKind.String
        )
        {
            return node.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static bool ReadFrameDefault(string behaviorRaw)
    {
        try
        {
            using var document = JsonDocument.Parse(behaviorRaw);
            if (
                document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("frameDefault", out var node)
            )
            {
                return node.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String
                        => !string.Equals(
                            node.GetString(),
                            "false",
                            StringComparison.OrdinalIgnoreCase
                        ),
                    _ => true,
                };
            }
        }
        catch { }
        return true;
    }

    private static string ScrubSecrets(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return raw;
            }
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (
                        string.Equals(property.Name, "apiKey", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            property.Name,
                            "password",
                            StringComparison.OrdinalIgnoreCase
                        )
                        || string.Equals(property.Name, "token", StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        continue;
                    }
                    property.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return raw;
        }
    }

    public sealed record LedgerEntry(string Name, string Title, string Summary, string Scope);

    private sealed class PersistedShape
    {
        public string ProvidersJson { get; set; } = "{}";
        public string ActiveProviderId { get; set; } = string.Empty;
        public string ActiveModelId { get; set; } = string.Empty;
        public string NetworkJson { get; set; } = "{}";
        public string BehaviorJson { get; set; } = "{\"frameDefault\":true}";
        public bool FrameDefault { get; set; } = true;
        public List<LedgerEntry>? Skills { get; set; }
        public List<LedgerEntry>? Memory { get; set; }
    }
}

/// <summary>Validation failure for configuration writes; maps to INVALID_PARAMS.</summary>
public sealed class LedgerValidationException : Exception
{
    public LedgerValidationException(string message)
        : base(message) { }
}
