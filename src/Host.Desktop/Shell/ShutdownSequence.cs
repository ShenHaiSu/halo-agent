namespace HaloAgent.Desktop.Shell;

/// <summary>
/// 受控关闭（唯一正确的退出路径）：
/// ①存窗口位置 → ②停引擎 → ③注销热键 → ④Dispose托盘 → ⑤⑥释放浏览器 → ⑦Cef.Shutdown。
/// 顺序不可改；fast 模式（关机/任务管理器）跳过所有等待。
/// </summary>
public static class ShutdownSequence
{
    private static readonly object Gate = new();
    private static bool _ran;

    public interface IShutdownTarget
    {
        void SavePlacementNow();
        Task StopEngineAsync(TimeSpan timeout);
        void DisposeHotkeys();
        void DisposeTray();
        void DisposeBrowser();
    }

    public static void Run(IShutdownTarget? form, bool fast = false)
    {
        lock (Gate)
        {
            if (_ran) return;
            _ran = true;
        }

        AppLog.Info($"开始关闭序列（fast={fast}）");

        // ① 保存窗口位置（纯本地，不受后续失败影响）。
        TryStep("保存窗口位置", () => form?.SavePlacementNow());

        // ② 停引擎。
        if (form is not null)
        {
            var timeout = fast ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(5);
            TryStepAsync("停止引擎", () => form.StopEngineAsync(timeout), timeout);
        }

        // ③ 注销热键（窗口销毁前）。
        TryStep("注销热键", () => form?.DisposeHotkeys());

        // ④ 托盘（消息循环结束前）。
        TryStep("释放托盘图标", () => form?.DisposeTray());

        // ⑤⑥ 浏览器。
        TryStep("释放浏览器", () => form?.DisposeBrowser());

        // ⑦ CEF（最后，此时不能再有 UI 操作）。
        TryStep("关闭 CEF", () =>
        {
            if (CefSharp.Cef.IsInitialized)
            {
                CefSharp.Cef.Shutdown();
                AppLog.Info("CEF 已关闭");
            }
        });

        AppLog.Info("关闭序列完成");
    }

    /// <summary>测试/验收用：重置“已执行”标记（生产路径只跑一次）。</summary>
    internal static void ResetForTest()
    {
        lock (Gate) { _ran = false; }
    }

    private static void TryStep(string name, Action? action)
    {
        if (action is null) return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"关闭步骤「{name}」失败（继续）：{ex.Message}");
        }
    }

    private static void TryStepAsync(string name, Func<Task> action, TimeSpan timeout)
    {
        try
        {
            var task = action();
            if (!task.Wait(timeout))
                AppLog.Warn($"关闭步骤「{name}」超时（{timeout.TotalSeconds}s），继续");
        }
        catch (AggregateException ex)
        {
            AppLog.Warn($"关闭步骤「{name}」失败（继续）：{ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"关闭步骤「{name}」失败（继续）：{ex.Message}");
        }
    }
}
