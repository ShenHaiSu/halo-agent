using Microsoft.Extensions.Logging;

namespace HaloAgent.Engine.Core;

/// <summary>
/// 引擎内核占位。P2 才实现 agent 循环 / 工具 / 权限。
/// P0 只需要它能编译、能被 Engine.Server 引用。
/// </summary>
public sealed class CorePlaceholder
{
    private readonly ILogger _log;

    public CorePlaceholder(ILogger<CorePlaceholder> log)
    {
        _log = log;
    }

    public void Touch()
    {
        _log.LogDebug("Core 占位被引用");
    }
}
