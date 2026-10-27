# CefSharp 跨代桌面端（Win7 / Win10 / Win11）架构铁律与开发规范

> **文档定位**：本规范为项目最高技术宪章，旨在确保在 **Windows 10/11 现代开发环境（纯 VS Code 工具链）** 下高效研发，同时保证产物在 **Windows 7 SP1 / Windows 10 / Windows 11** 全系操作系统上零崩溃、高性能运行。

---

## 一、 架构红线与版本铁律（严禁突破）

为兼顾 Windows 7 兼容性与现代工程化体验，以下核心底座版本**永久锁定**，未经架构评审严禁擅自升级：

| 核心层级 | 锁定选型与版本 | 强制约束说明 |
| :--- | :--- | :--- |
| **浏览器内核底座** | **`CefSharp.WinForms` = `109.1.110`** | Chromium 109 是官方支持 Win7 的最终绝唱版本。**严禁升级至 110+**，否则在 Win7 下启动即因缺失系统 API 直接崩溃。 |
| **原生窗口宿主** | **WinForms (`System.Windows.Forms`)** | **严禁使用 `CefSharp.Wpf`**。WPF 版采用离屏渲染（OSR），在 Win7 老旧显卡/核显驱动下极易黑屏、闪烁且性能低下；WinForms 版基于原生 HWND 句柄，渲染性能与兼容性最佳。 |
| **.NET 目标框架** | **`.NET 6.0` (`net6.0-windows`)** <br>*(备选：`.NET Framework 4.7.2/4.8`)* | `.NET 6` 是微软官方支持 Win7 的最后一个现代跨平台 SDK 版本（支持 `dotnet watch` 与 VS Code 工具链）。**严禁升级至 .NET 7 / 8 / 9**。 |
| **前端构建目标** | **`build.target = 'chrome109'`** | 前端源码可使用最新 Vue 3 / React 19 / TypeScript 5+，但 Vite 打包目标必须锁定为 `chrome109`，以自动完成高版本语法与 CSS 降级。 |
| **目标 CPU 架构** | **`x86`（或 `x64` 独立分发）** | **严禁使用 `AnyCPU` 裸奔**（除非开启 `CefSharpAnyCpuSupport` 并配齐双架构非托管目录）。若需通吃 32 位与 64 位老旧 Win7，默认优先构建 **`x86` 自包含包**，或提供 `x86` / `x64` 独立安装包。 |

---

## 二、 开发环境标准配置（纯 VS Code 零 Visual Studio 方案）

开发端（Windows 10 / 11）无需安装庞大的 Visual Studio IDE，统一采用轻量级工具链：

1. **必备 SDK 与运行时**：
   * **.NET 6.0 SDK**（注意：即便本机已装 .NET 8/9 SDK，也建议安装 .NET 6.0 Runtime/SDK 或通过 `global.json` 规范构建行为）。
   * **Node.js (LTS v20/v22+)** + **pnpm**（管理前端工程）。
2. **VS Code 必装扩展**：
   * `C# Dev Kit`（提供现代 C# 智能感知、构建与断点调试）。
   * 前端对应技术栈插件（如 `Vue - Official` / `ESLint` / `Prettier` / `Tailwind CSS IntelliSense`）。
3. **推荐目录结构**：
   ```text
   ProjectRoot/
   ├── src/
   │   ├── Host.Desktop/              # C# WinForms + CefSharp 宿主工程 (.csproj)
   │   │   ├── Bridge/                # 暴露给前端 JS 调用的 C# 桥接类
   │   │   ├── Handlers/              # CEF 生命周期、菜单、下载、请求拦截处理器
   │   │   ├── Services/              # 后端核心业务、硬件调用、本地数据库逻辑
   │   │   ├── Program.cs             # 进程入口与 CEF 全局初始化
   │   │   └── MainForm.cs            # 全屏承载 ChromiumWebBrowser 的无冗余宿主窗体
   │   └── Web.Client/                # 前端现代工程 (Vite + Vue3/React + TS)
   │       ├── src/
   │       ├── vite.config.ts         # 锁定 target: 'chrome109'
   │       └── package.json
   ├── build/                         # 打包脚本 (Inno Setup / NSIS) 与前置依赖 (VC++ 运行库)
   └── ARCHITECTURE_STANDARD.md       # 本规范文档
   ```

---

## 三、 C# 宿主端开发规范与防崩溃铁律

### 1. 标准 `.csproj` 锁定配置模板
在 `Host.Desktop.csproj` 中必须显式固定版本号与目标平台：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <!-- 铁律：锁死 net6.0-windows，禁止升级 net7.0/net8.0 -->
    <TargetFramework>net6.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <!-- 明确指定平台架构，避免 CEF 非托管 DLL 加载失败 -->
    <PlatformTarget>x64</PlatformTarget>
    <Platforms>x64;x86</Platforms>
  </PropertyGroup>

  <ItemGroup>
    <!-- 铁律：锁死 109.1.110，禁止更新 -->
    <PackageReference Include="CefSharp.Common.NETCore" Version="109.1.110" />
    <PackageReference Include="CefSharp.WinForms.NETCore" Version="109.1.110" />
  </ItemGroup>
</Project>
```

### 2. Win7 防黑屏与高分屏（DPI）初始化规范
Win7 存在大量老旧显卡驱动，且 Win7/Win10/Win11 的多显示器 DPI 缩放机制存在代差。`Program.cs` 初始化 **必须** 包含以下兜底配置：

```csharp
using System;
using System.IO;
using System.Windows.Forms;
using CefSharp;
using CefSharp.WinForms;

namespace Host.Desktop;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // 1. 高 DPI 自适应（兼顾 Win10/11 高分屏与 Win7 传统缩放）
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var settings = new CefSettings
        {
            Locale = "zh-CN",
            AcceptLanguageList = "zh-CN,zh;q=0.9,en;q=0.8",
            // 持久化缓存目录（IndexedDB / LocalStorage 落盘必配）
            CachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YourAppName", "CefCache"
            )
        };

        // 2. Win7 显卡驱动防崩溃铁律：检测 Win7 (NT 6.1) 或配置项强制关闭 GPU 硬件加速
        bool isWin7 = Environment.OSVersion.Version.Major == 6 && Environment.OSVersion.Version.Minor == 1;
        if (isWin7 || File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "disable-gpu.flag")))
        {
            settings.DisableGpuAcceleration();
            settings.CefCommandLineArgs.Add("disable-gpu-compositing");
            settings.CefCommandLineArgs.Add("disable-gpu-vsync");
        }

        // 3. 禁用 Chromium 109 在老系统上的多余后台网络探测与安全弹窗干扰
        settings.CefCommandLineArgs.Add("disable-features", "CalculateNativeWinOcclusion,WinUseBrowserSpellChecker");
        settings.CefCommandLineArgs.Add("no-proxy-server"); // 视业务需求决定是否绕过系统异常代理

        // 4. 注册自定义本地安全协议（替代 file://，彻底解决跨域与 ES Module 加载问题）
        settings.RegisterScheme(new CefCustomScheme
        {
            SchemeName = "app",
            DomainName = "local",
            SchemeHandlerFactory = new FolderSchemeHandlerFactory(
                rootFolder: Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot"),
                hostName: "local",
                defaultPage: "index.html"
            ),
            IsStandard = true,
            IsLocal = true,
            IsSecure = true,
            IsCorsEnabled = true
        });

        Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null);

        Application.Run(new MainForm());

        // 5. 退出时优雅释放 CEF 句柄，防止残留僵尸进程
        Cef.Shutdown();
    }
}
```

### 3. 本地静态资源加载铁律（严禁裸用 `file://`）
* **禁止直接加载 `file:///.../index.html`**：现代前端打包产物（Vite ES Module、Web Worker、字体跨域、Fetch 本地 JSON）在 `file://` 协议下会触发浏览器严苛的 CORS 阻断。
* **强制标准**：
  * **开发态（Development）**：C# 读取环境变量，直接 `chromeBrowser.LoadUrl("http://localhost:5173")`，并默认开启 `chromeBrowser.ShowDevTools()` 支持热更新调试。
  * **生产态（Production）**：前端打包输出至宿主目录的 `wwwroot`，C# 通过上述注册的 `app://local/index.html`（`FolderSchemeHandlerFactory`）加载，享受等同于 `https://` 的完整 Web 安全上下文。

---

## 四、 前后端通信（JS Bridge）与线程安全规范

CefSharp 的通信极其强大，但若违反线程规范，极易引发死锁或界面卡死：

### 1. 接口注入规范
使用 `JavascriptObjectRepository.Register` 暴露异步对象，统一挂载在前端 `window.hostBridge` 命名空间下：
```csharp
// C# 侧注册（必须在浏览器初始化完成前或创建时绑定）
chromeBrowser.JavascriptObjectRepository.Settings.LegacyBindingEnabled = false;
chromeBrowser.JavascriptObjectRepository.Register("hostBridge", new SystemBridgeService(), options: BindingOptions.DefaultBinder);
```
```typescript
// 前端 TS 侧调用（开机自动绑定或按需绑定）
await CefSharp.BindObjectAsync("hostBridge");
const result = await window.hostBridge.getSystemInfo();
```

### 2. 三大通信铁律
1. **严禁同步阻塞调用**：C# 暴露给 JS 的所有耗时操作（文件 IO、串口/硬件读写、数据库查询、HTTP 请求）**必须返回 `Task<T>`**，严禁在桥接方法中阻塞主线程。
2. **UI 线程跨线程红线**：CefSharp 的 JS 桥接调用运行在 **CEF IO / Worker 后台线程**，**不在 WinForms 主 UI 线程**！如果需要在 C# 桥接方法内操作 `MainForm`（如最小化、最大化、改窗口标题、弹原生对话框），**必须**通过 `form.Invoke(() => { ... })` 切回主线程，否则必报跨线程异常。
3. **复杂数据结构序列化**：前后端传递复杂 DTO 时，统一遵循 `camelCase`（小驼峰）命名，对于高频大数据量传输（如实时监控流、大文件流），优先通过自定义 Scheme 流响应或本地 WebSocket 回环传输，避免塞爆 IPC 序列化通道。

---

## 五、 前端工程化开发规范（Web.Client）

虽然底层锁定了 Chromium 109，但前端研发保持最新技术栈，仅需在构建层遵守以下约束：

### 1. `vite.config.ts` 强制标准配置
```typescript
import { defineConfig } from 'vite';

export default defineConfig({
  base: './', // 或配合 app://local/ 使用 '/'
  build: {
    // 铁律：锁定编译目标为 chrome109，自动转译高版本 JS/CSS
    target: 'chrome109',
    outDir: '../Host.Desktop/wwwroot',
    emptyOutDir: true,
    sourcemap: false,
  },
  css: {
    // 若使用 LightningCSS 或 PostCSS，确保目标同样对齐 Chrome 109
  }
});
```

### 2. 前端 API 与兼容性注意事项
* **放心使用的特性**：Vue 3.5+ / React 19、TypeScript 5.x、Tailwind CSS、CSS Grid / Flexbox、`@container` 容器查询、`:has()` 选择器、WebGL 2.0、Web Audio、WebSockets、IndexedDB。
* **需规避或 Polyfill 的超前特性**：
  * **严禁强依赖 WebGPU**（Chrome 113+ 才开启，且 Win7 显卡不支持），3D 渲染一律使用 **WebGL / WebGL2**。
  * **避免直接使用原生 `Object.groupBy` / `Promise.withResolvers` / `Array.fromAsync`**（ES2024+ 新增 API）：若业务代码使用，必须在入口 `main.ts` 引入 `core-js` 对应 Polyfill。
  * **禁止依赖系统级暗黑模式媒体查询 (`prefers-color-scheme`) 的自动联动**：Win7 系统本身无全局暗黑模式 API，主题切换必须在应用层内部（通过 CSS Variables / Class）自行控制。

---

## 六、 打包、分发与 Win7 落地预检清单（Release Checklist）

将开发环境（Win10/11）构建出的程序分发到客户现场（尤其是精简版/内网版 Win7）时，必须通过以下交付清单：

### 1. 编译发布命令（自包含免安装 .NET 运行时）
发布时必须采用 **Self-Contained（自包含）** 模式，避免要求客户在 Win7 上手动安装 .NET 6 Runtime：
```bash
# 构建前端生产包
cd src/Web.Client && pnpm build

# 构建 C# 自包含发行包（以 64位 为例；若需通吃 32位 Win7，将 win-x64 改为 win-x86）
cd ../Host.Desktop
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
```
> **注意**：`PublishSingleFile`（单文件发布）对 CefSharp 非托管 C++ 资源（`libcef.dll`、`icudtl.dat`、`*.pak`、子进程 `CefSharp.BrowserSubprocess.exe`）支持极差，**严禁开启 `PublishSingleFile=true`**，必须使用安装包制作工具（Inno Setup / NSIS）将发布目录打包为标准 `Setup.exe`。

### 2. Win7 客户机运行环境强依赖（安装器必须兜底）
客户现场的裸机 Win7 若要正常启动 Chromium 109 与 .NET 6，以下依赖缺一不可：
1. **操作系统基线**：必须是 **Windows 7 SP1**（Build 7601）。
2. **系统安全补丁 `KB2533623`**：.NET 6 与现代 C++ 动态库安全加载 API（`AddDllDirectory`）强依赖该补丁。若极端老旧裸机未打此补丁，程序启动会报 `EntryPointNotFoundException: AddDllDirectory`。
3. **VC++ 运行库 (`VC_redist.x86.exe` / `VC_redist.x64.exe` 2015-2022)**：
   * `CefSharp 109` 底层非托管 DLL 强依赖 VC++ 运行时。
   * **最佳实践**：在项目 `AppLocal` 目录下直接随包附带 `vcruntime140.dll`、`vcruntime140_1.dll`、`msvcp140.dll`、`concrt140.dll` 及 Universal CRT (UCRT) 核心 DLL，或在 Inno Setup 安装脚本中静默执行 `VC_redist.exe /install /quiet /norestart`。
4. **TLS 1.2 协议开启（Win7 网络请求防失败）**：
   * Win7 默认未在系统注册表层面默认启用 TLS 1.2，若由 **C# `HttpClient`** 直接发起 HTTPS 请求访问现代云端接口，会报 SSL/TLS 握手失败（注：Chromium 109 内部自带 BoringSSL，前端 `fetch/axios` 不受影响，但 C# 后端受影响）。
   * **C# 侧必加代码**：在 `Program.cs` 启动最顶部显式声明：
     ```csharp
     System.Net.ServicePointManager.SecurityProtocol =
         System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;
     ```
5. **中文字体回退机制**：
   * Win10/11 默认拥有新版 `Microsoft YaHei UI` 和 `Segoe UI Emoji`，而 Win7 只有旧版 `Microsoft YaHei` 且不支持彩色 Emoji 字体。
   * 前端全局 `font-family` 必须配置合理回退栈，严禁使用纯 Emoji 字符充当功能图标，**所有图标一律使用 SVG（如 Lucide / Iconify）**。
