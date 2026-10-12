# Stage 01 - 02：CefSharp 109 Win7 防崩宿主与双向 IPC 通信实现方案

> **所属阶段**：Stage 01
> **核心目标**：严格落实 `CEFSHARP_ARCHITECTURE_STANDARD.md` 第三、四、六章要求，构建零崩溃的 C# WinForms 宿主与高性能前后端通信底座。

---

## 一、 工程锁定配置（`global.json` 与 `Host.Desktop.csproj`）

### 1. 根目录 `global.json`
防止多 SDK 环境下误用 .NET 7/8/9 编译器特性：
```json
{
  "sdk": {
    "version": "6.0.428",
    "rollForward": "latestPatch",
    "allowPrerelease": false
  }
}
```

### 2. `src/Host.Desktop/Host.Desktop.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <!-- 铁律：锁死 net6.0-windows，禁止升级 net7.0/net8.0 -->
    <TargetFramework>net6.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PlatformTarget>x64</PlatformTarget>
    <Platforms>x64;x86</Platforms>
    <ApplicationIcon Condition="Exists('Resources\app.ico')">Resources\app.ico</ApplicationIcon>
  </PropertyGroup>

  <ItemGroup>
    <!-- 铁律：锁死 CefSharp 109.1.110，Chromium 109 为支持 Win7 最终版 -->
    <PackageReference Include="CefSharp.Common.NETCore" Version="109.1.110" />
    <PackageReference Include="CefSharp.WinForms.NETCore" Version="109.1.110" />
    <!-- Win7 DPAPI 加密支持（用于 Stage 02 API Key 加密存储） -->
    <PackageReference Include="System.Security.Cryptography.ProtectedData" Version="6.0.0" />
  </ItemGroup>
</Project>
```

---

## 二、 Win7 防崩溃宿主初始化（`Program.cs`）

### 1. 关键底层陷阱规避：Win7 下 TLS 1.2 / 1.3 安全启用
`CEFSHARP_ARCHITECTURE_STANDARD.md` 第 240 行要求在 `Program.cs` 顶部开启 TLS 1.2/1.3。
**底层架构细化**：在未打齐最新 Schannel 补丁的 Windows 7 SP1 上，直接将 `SecurityProtocolType.Tls13` 赋值给 `ServicePointManager.SecurityProtocol` 可能会抛出 `NotSupportedException`。因此必须采用**渐进式安全协商策略**：

```csharp
private static void ConfigureWin7CompatibleTls()
{
    // Win7 SP1 必开 TLS 1.2，否则 C# HttpClient 请求现代 HTTPS LLM 接口必败
    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
    try
    {
        // 在支持 TLS 1.3 的 Win10/Win11 上追加开启 TLS 1.3，Win7 不支持则静默保留 Tls12
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls13;
    }
    catch (NotSupportedException)
    {
        // Win7 正常回退，仅保持 TLS 1.2
    }
}
```

### 2. `Program.cs` 完整启动流水线
```csharp
using System.Net;
using CefSharp;
using CefSharp.SchemeHandler;
using CefSharp.WinForms;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // 0. 强制开启 TLS 1.2 (兼容 Win7 SP1)
        ConfigureWin7CompatibleTls();

        // 1. 高 DPI 自适应（兼顾 Win10/11 高分屏与 Win7 传统缩放）
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 2. 初始化 C 盘可配置存储引导与滚动日志引擎
        var storageManager = StoragePathManager.Initialize();
        HaloLogger.Initialize(storageManager);
        HaloLogger.App.Info($"HaloAgent starting. OS={Environment.OSVersion}, DataRoot={storageManager.CurrentDataRoot}");

        // 3. 全局未捕获异常兜底，写入 C 盘日志防止静默闪退
        Application.ThreadException += (_, e) => HaloLogger.App.Error("UI Thread Exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            HaloLogger.App.Error("AppDomain Unhandled Exception", e.ExceptionObject as Exception);

        // 4. 配置 CefSettings（CachePath 指向用户配置的 DataRoot\cef-cache）
        var settings = new CefSettings
        {
            Locale = "zh-CN",
            AcceptLanguageList = "zh-CN,zh;q=0.9,en;q=0.8",
            CachePath = storageManager.CefCacheDirectory,
            LogFile = Path.Combine(storageManager.LogsDirectory, "cef-native.log"),
            LogSeverity = LogSeverity.Warning
        };

        // 5. Win7 显卡驱动防崩溃铁律：检测 Win7 (NT 6.1) 或 disable-gpu.flag 强制关闭 GPU 硬件加速
        bool isWin7 = Environment.OSVersion.Version.Major == 6 && Environment.OSVersion.Version.Minor == 1;
        bool hasDisableGpuFlag = File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "disable-gpu.flag"));
        if (isWin7 || hasDisableGpuFlag)
        {
            settings.DisableGpuAcceleration();
            settings.CefCommandLineArgs.Add("disable-gpu-compositing");
            settings.CefCommandLineArgs.Add("disable-gpu-vsync");
            HaloLogger.App.Info($"GPU Acceleration disabled (isWin7={isWin7}, flag={hasDisableGpuFlag})");
        }

        // 6. 禁用 Chromium 109 在老系统上的多余后台网络探测与安全弹窗干扰
        settings.CefCommandLineArgs.Add("disable-features", "CalculateNativeWinOcclusion,WinUseBrowserSpellChecker");

        // 7. 注册自定义本地安全协议 app://local（替代 file://，彻底解决 CORS 与 ES Module 阻断）
        var wwwrootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
        Directory.CreateDirectory(wwwrootPath);
        settings.RegisterScheme(new CefCustomScheme
        {
            SchemeName = "app",
            DomainName = "local",
            SchemeHandlerFactory = new FolderSchemeHandlerFactory(
                rootFolder: wwwrootPath,
                hostName: "local",
                defaultPage: "index.html"
            ),
            IsStandard = true,
            IsLocal = true,
            IsSecure = true,
            IsCorsEnabled = true
        });

        Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null);

        try
        {
            Application.Run(new MainForm(storageManager));
        }
        finally
        {
            // 8. 退出时优雅释放日志队列与 CEF 句柄，防止残留 CefSharp.BrowserSubprocess.exe 僵尸进程
            HaloLogger.Shutdown();
            Cef.Shutdown();
        }
    }
}
```

---

## 三、 双向 IPC 桥接与流式事件总线设计

### 1. 统一返回信封 `BridgeResponse<T>`
为避免 C# 异常直接抛向 JS 导致 Promise 堆栈丢失，所有 `window.hostBridge` 方法统一返回 `Task<BridgeResponse<T>>`（遵循小驼峰 `camelCase` 序列化）：

```csharp
namespace Host.Desktop.Core.Models;

public sealed class BridgeResponse<T>
{
    public bool Ok { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public T? Data { get; init; }

    public static BridgeResponse<T> Success(T data) => new() { Ok = true, Data = data };
    public static BridgeResponse<T> Fail(string code, string message) =>
        new() { Ok = false, Code = code, Message = message };
}
```

### 2. JS -> C# 异步桥接门面（`HostBridgeFacade.cs`）
在 `MainForm` 创建 `ChromiumWebBrowser` 时挂载：
```csharp
chromeBrowser.JavascriptObjectRepository.Settings.LegacyBindingEnabled = false;
chromeBrowser.JavascriptObjectRepository.Register(
    "hostBridge",
    new HostBridgeFacade(this, _storageManager),
    options: BindingOptions.DefaultBinder
);
```
**跨线程铁律落地**：当前端通过 `hostBridge.selectDirectoryDialog()`（选择新工作路径或选择 C 盘日志新存储目录）或控制窗口最小化/最大化时，因为 `HostBridgeFacade` 方法运行在 CEF IO/Worker 线程，**必须封装统一的 UI 线程调度辅助方法**：

```csharp
public Task<T> InvokeOnUiThreadAsync<T>(Func<T> action)
{
    var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    if (_mainForm.IsDisposed)
    {
        tcs.SetCanceled();
        return tcs.Task;
    }

    if (_mainForm.InvokeRequired)
    {
        _mainForm.BeginInvoke(new Action(() =>
        {
            try { tcs.SetResult(action()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }));
    }
    else
    {
        try { tcs.SetResult(action()); }
        catch (Exception ex) { tcs.SetException(ex); }
    }
    return tcs.Task;
}
```

### 3. C# -> JS 高频事件节流推送器（`JsEventDispatcher.cs`）
在 Stage 03（LLM 流式输出）与 Stage 05（命令行 `cmd` 实时 stdout 输出）中，每秒可能产生上百个文本增量片段。若每个 chunk 都调用一次 `ExecuteJavaScriptAsync`，会导致 IPC 通道拥塞与页面卡顿。

因此设计 **`JsEventDispatcher` 批量节流总线**：
1. 后端任何模块调用 `JsEventDispatcher.Publish(string eventType, object payload)` 将事件压入 `ConcurrentQueue<HostEventEnvelope>`。
2. 内部 `PeriodicTimer`（或 `.NET 6` 兼容的 `System.Threading.Timer`，周期 `16ms`，约 60fps）定期将队列中积累的多条事件打包为单个 JSON 数组。
3. 通过 `browser.ExecuteJavaScriptAsync($"window.__haloReceiveHostEvents({jsonBatch});")` 一次性投递给前端，前端按序分发给 Pinia Store。

---

## 四、 CEF 安全处理器（`Handlers/`）

1. **`CustomMenuHandler` (`IContextMenuHandler`)**：
   * 生产环境下屏蔽默认浏览器无用的“打印”、“查看源代码”、“另存为”菜单，仅在输入框保留“剪切 / 复制 / 粘贴 / 全选”，在开发态保留“检查元素（Inspect）”。
2. **`CustomLifeSpanHandler` (`ILifeSpanHandler`)**：
   * 拦截 `target="_blank"` 或 `window.open` 弹出新窗口行为，改为调用系统默认浏览器打开外部文档链接，防止弹出空白原生 WinForms 窗口。
