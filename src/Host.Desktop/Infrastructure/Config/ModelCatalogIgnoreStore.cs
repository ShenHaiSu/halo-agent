using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Infrastructure.Config;

/// <summary>
/// 供应商模型忽视表持久化：{ConfigDirectory}/model-catalog.json。
/// 只存用户明确忽视的模型 Id 集合，不碰 providers.json 调用配置。
/// 大小写折叠存储（存 trim 后原值，比较用 OrdinalIgnoreCase），缺失或损坏一律回空集。
/// </summary>
public sealed class ModelCatalogIgnoreStore
{
    private readonly StoragePathManager _storagePathManager;
    private readonly object _syncLock = new();

    public string IgnoreFilePath =>
        Path.Combine(_storagePathManager.ConfigDirectory, "model-catalog.json");

    public ModelCatalogIgnoreStore(StoragePathManager storagePathManager)
    {
        _storagePathManager =
            storagePathManager ?? throw new ArgumentNullException(nameof(storagePathManager));
    }

    public HashSet<string> Load(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        lock (_syncLock)
        {
            var doc = AtomicFileWriter.ReadJsonOrDefault<ModelCatalogIgnoreDocument>(
                IgnoreFilePath
            );
            if (
                doc?.Ignored == null
                || !doc.Ignored.TryGetValue(providerId.Trim(), out List<string>? ids)
                || ids == null
            )
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? raw in ids)
            {
                string id = (raw ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(id))
                {
                    set.Add(id);
                }
            }

            return set;
        }
    }

    public void Save(string? providerId, IEnumerable<string>? modelIds)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        lock (_syncLock)
        {
            var doc =
                AtomicFileWriter.ReadJsonOrDefault<ModelCatalogIgnoreDocument>(IgnoreFilePath)
                ?? new ModelCatalogIgnoreDocument();
            doc.Version = 1;
            doc.Ignored ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            doc.UpdatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            var cleaned = new SortedSet<string>(StringComparer.Ordinal);
            if (modelIds != null)
            {
                foreach (string? raw in modelIds)
                {
                    string id = (raw ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    bool duplicate = false;
                    foreach (string existing in cleaned)
                    {
                        if (string.Equals(existing, id, StringComparison.OrdinalIgnoreCase))
                        {
                            duplicate = true;
                            break;
                        }
                    }

                    if (!duplicate)
                    {
                        cleaned.Add(id);
                    }
                }
            }

            doc.Ignored[providerId.Trim()] = cleaned.ToList();
            AtomicFileWriter.WriteJsonAtomic(IgnoreFilePath, doc);
        }
    }

    public void RemoveProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        lock (_syncLock)
        {
            var doc = AtomicFileWriter.ReadJsonOrDefault<ModelCatalogIgnoreDocument>(
                IgnoreFilePath
            );
            if (doc?.Ignored == null)
            {
                return;
            }

            if (doc.Ignored.Remove(providerId.Trim()))
            {
                doc.UpdatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                AtomicFileWriter.WriteJsonAtomic(IgnoreFilePath, doc);
            }
        }
    }

    private sealed class ModelCatalogIgnoreDocument
    {
        public int Version { get; set; } = 1;

        public Dictionary<string, List<string>> Ignored { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public string UpdatedAtUtc { get; set; } = string.Empty;
    }
}
