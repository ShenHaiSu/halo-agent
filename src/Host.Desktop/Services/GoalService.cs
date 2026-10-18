using System.Collections.Concurrent;
using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Services;

/// <summary>need16 GoalService：goal.json 边车读写（原子写 + 按 ws/sess 串行 + 纯函数 ApplyAction 可单测）。</summary>
public sealed class GoalService
{
    private readonly string _dataRoot;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public GoalService(StoragePathManager storage)
        : this(storage?.CurrentDataRoot ?? Path.GetTempPath()) { }

    public GoalService(string dataRoot)
    {
        _dataRoot = string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : dataRoot;
    }

    private SemaphoreSlim LockFor(string ws, string sess)
        => _locks.GetOrAdd($"{ws?.Trim()}/{sess?.Trim()}", _ => new SemaphoreSlim(1, 1));

    public string GoalFilePath(string workspaceId, string sessionId)
    {
        string dir = Path.Combine(_dataRoot, "sessions", workspaceId ?? string.Empty, sessionId ?? string.Empty);
        return Path.Combine(dir, "goal.json");
    }

    /// <summary>缺失→空 Goal；损坏→抛 GoalCorruptException。</summary>
    public SessionGoal LoadGoal(string workspaceId, string sessionId)
    {
        var (goal, corrupted) = TryLoadGoal(workspaceId, sessionId);
        if (corrupted)
            throw new GoalCorruptException(GoalFilePath(workspaceId, sessionId), "目标文件损坏，已视为空");
        return goal;
    }

    public (SessionGoal goal, bool corrupted) TryLoadGoal(string workspaceId, string sessionId)
    {
        string path = GoalFilePath(workspaceId, sessionId);
        if (!File.Exists(path)) return (new SessionGoal(), false);
        try
        {
            string raw = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(raw)) return (new SessionGoal(), false);
            var goal = JsonSerializer.Deserialize<SessionGoal>(raw, HaloJsonOptions.IndentedCamelCase);
            if (goal == null) return (new SessionGoal(), false);
            goal.Milestones ??= new List<GoalMilestone>();
            goal.Percent = Math.Clamp(goal.Percent, 0, 100);
            return (goal, false);
        }
        catch
        {
            return (new SessionGoal(), true);
        }
    }

    public SessionGoal SaveGoal(string workspaceId, string sessionId, SessionGoal goal, string updatedBy)
    {
        if (goal == null) goal = new SessionGoal();
        goal.Milestones ??= new List<GoalMilestone>();
        goal.Percent = Math.Clamp(goal.Percent, 0, 100);
        goal.UpdatedAtUtc = DateTime.UtcNow;
        goal.UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? "main" : updatedBy.Trim();
        var l = LockFor(workspaceId, sessionId);
        l.Wait();
        try
        {
            AtomicFileWriter.WriteJsonAtomic(GoalFilePath(workspaceId, sessionId), goal);
        }
        finally { l.Release(); }
        return goal;
    }

    /// <summary>纯函数：set/update/progress/add_milestone/complete_milestone/clear/get。</summary>
    public SessionGoal ApplyAction(SessionGoal cur, string action, JsonElement args)
    {
        cur ??= new SessionGoal();
        cur.Milestones ??= new List<GoalMilestone>();
        string act = (action ?? "get").Trim().ToLowerInvariant();
        static string Str(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object) return string.Empty;
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString() ?? string.Empty;
            return string.Empty;
        }
        switch (act)
        {
            case "get":
                return cur;
            case "set":
            {
                string title = Str(args, "title");
                string desc = Str(args, "description");
                string note = Str(args, "note");
                int percent = cur.Percent;
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("percent", out var pv) && pv.ValueKind == JsonValueKind.Number && pv.TryGetInt32(out int pi))
                    percent = Math.Clamp(pi, 0, 100);
                var ms = new List<GoalMilestone>();
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("milestones", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        string t = item.ValueKind == JsonValueKind.String ? (item.GetString() ?? string.Empty) : string.Empty;
                        if (string.IsNullOrWhiteSpace(t)) continue;
                        ms.Add(new GoalMilestone { Title = t.Trim(), Done = false, UpdatedAtUtc = DateTime.UtcNow });
                    }
                }
                cur.Title = title ?? string.Empty;
                cur.Description = desc ?? string.Empty;
                cur.Milestones = ms;
                cur.Percent = percent;
                cur.Note = note ?? string.Empty;
                return cur;
            }
            case "update":
            {
                if (args.ValueKind == JsonValueKind.Object)
                {
                    if (args.TryGetProperty("title", out var tv) && tv.ValueKind == JsonValueKind.String) cur.Title = tv.GetString() ?? string.Empty;
                    if (args.TryGetProperty("description", out var dv) && dv.ValueKind == JsonValueKind.String) cur.Description = dv.GetString() ?? string.Empty;
                    if (args.TryGetProperty("note", out var nv) && nv.ValueKind == JsonValueKind.String) cur.Note = nv.GetString() ?? string.Empty;
                }
                return cur;
            }
            case "progress":
            {
                if (args.ValueKind == JsonValueKind.Object)
                {
                    if (args.TryGetProperty("percent", out var pv) && pv.ValueKind == JsonValueKind.Number && pv.TryGetInt32(out int pi))
                        cur.Percent = Math.Clamp(pi, 0, 100);
                    if (args.TryGetProperty("note", out var nv) && nv.ValueKind == JsonValueKind.String) cur.Note = nv.GetString() ?? string.Empty;
                }
                return cur;
            }
            case "add_milestone":
            {
                string t = Str(args, "title");
                if (string.IsNullOrWhiteSpace(t))
                    throw new GoalValidationException("GOAL_VALIDATION", "add_milestone 需要 title");
                cur.Milestones.Add(new GoalMilestone { Title = t.Trim(), Done = false, UpdatedAtUtc = DateTime.UtcNow });
                return cur;
            }
            case "complete_milestone":
            {
                string key = Str(args, "milestone");
                if (string.IsNullOrWhiteSpace(key)) key = Str(args, "id");
                if (string.IsNullOrWhiteSpace(key)) key = Str(args, "title");
                if (string.IsNullOrWhiteSpace(key))
                    throw new GoalValidationException("GOAL_VALIDATION", "complete_milestone 需要 milestone（id 或 title）");
                string k = key.Trim();
                var hit = cur.Milestones.FirstOrDefault(m => string.Equals(m.Id, k, StringComparison.OrdinalIgnoreCase))
                    ?? cur.Milestones.FirstOrDefault(m => string.Equals(m.Title, k, StringComparison.OrdinalIgnoreCase));
                if (hit == null)
                    throw new GoalValidationException("GOAL_MILESTONE_MISSING", $"未找到里程碑：{k}");
                hit.Done = true;
                hit.UpdatedAtUtc = DateTime.UtcNow;
                int total = cur.Milestones.Count;
                int done = cur.Milestones.Count(m => m.Done);
                if (total > 0) cur.Percent = (int)Math.Round(done * 100.0 / total);
                return cur;
            }
            case "clear":
                return new SessionGoal();
            default:
                throw new GoalValidationException("GOAL_VALIDATION", $"未知 action：{action}（允许 get/set/update/progress/add_milestone/complete_milestone/clear）");
        }
    }
}

public sealed class GoalValidationException : Exception
{
    public string Code { get; }
    public GoalValidationException(string code, string message) : base(message) { Code = code ?? "GOAL_VALIDATION"; }
}
