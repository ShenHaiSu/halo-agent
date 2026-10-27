namespace HaloAgent.Engine.Storage;

/// <summary>
/// 存储层占位。P2 才实现会话 JSONL / 记忆 / undo 快照 / 索引。
/// P0 只需要它能编译、能被 Engine.Core 引用。
/// </summary>
public static class StoragePlaceholder
{
    /// <summary>返回默认数据目录（与引擎约定一致，见 EngineStartupOptions）。</summary>
    public static string DefaultDataDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Path.Combine(Path.GetTempPath(), "haloagent");
        return Path.Combine(home, ".haloagent");
    }
}
