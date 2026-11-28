using Host.Desktop.Core.Models;

namespace Host.Desktop.ModelCatalog;

/// <summary>
/// 快照差分器（纯函数）：快照 × 已有 models[] × 忽略表 → 新增 / 已存在 / 已忽视三态。
/// 忽略优先于已存在：脏数据下（已删模型仍在忽视表）忽视胜出，避免已删模型复活。
/// 大小写折叠：Parser 去重用 Ordinal（与模型 Id 语义一致），差分比较用 OrdinalIgnoreCase
///（与 SetActiveModel/Upsert 的查找语义一致）。
/// </summary>
public static class ProviderModelDiffCalculator
{
    public static void ApplyDiff(
        IList<DiscoveredProviderModel> discovered,
        IReadOnlyList<ModelConfig>? existing,
        IReadOnlyCollection<string>? ignored
    )
    {
        if (discovered == null || discovered.Count == 0)
        {
            return;
        }

        var ignoredSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ignored != null)
        {
            foreach (string? raw in ignored)
            {
                string id = (raw ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(id))
                {
                    ignoredSet.Add(id);
                }
            }
        }

        var existingMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (existing != null)
        {
            foreach (ModelConfig? m in existing)
            {
                if (m == null)
                {
                    continue;
                }

                string id = (m.Id ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(id) || existingMap.ContainsKey(id))
                {
                    continue;
                }

                existingMap[id] = m.Enabled;
            }
        }

        foreach (DiscoveredProviderModel item in discovered)
        {
            string id = (item.Id ?? string.Empty).Trim();
            if (ignoredSet.Contains(id))
            {
                item.State = CatalogModelState.Ignored;
                item.ExistsEnabled = false;
                continue;
            }

            if (existingMap.TryGetValue(id, out bool enabled))
            {
                item.State = CatalogModelState.Existing;
                item.ExistsEnabled = enabled;
                continue;
            }

            item.State = CatalogModelState.New;
            item.ExistsEnabled = false;
        }
    }
}
