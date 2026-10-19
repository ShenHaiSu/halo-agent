# Stage 01 - 01：跨代宿主底座、C盘可配置存储与前后端脚手架（前置检查与当期目标）

> **所属阶段**：Stage 01（基础底座与存储日志中枢）
> **依赖前置**：无（项目首发阶段）
> **关联规范**：[`docs/CEFSHARP_ARCHITECTURE_STANDARD.md`](../../CEFSHARP_ARCHITECTURE_STANDARD.md) 第一、二、三、四、五章

---

## 一、 前置环境检查（Preflight Checklist）

在启动 Stage 01 编码前，必须通过以下命令行校验，确保开发环境符合架构红线：

### 1. 工具链版本校验命令
```powershell
# 1. 验证 .NET 6.0 SDK 已安装（严禁使用 net7.0/net8.0 作为 TargetFramework）
dotnet --list-sdks
# 预期输出包含：6.0.x (如 6.0.428)

# 2. 验证 Node.js 与 pnpm 版本
node -v   # 预期：>= v20.x (当前 v22.23.3)
pnpm -v   # 预期：>= 9.x / 10.x

# 3. 验证 VS Code 关键插件
code --list-extensions
# 预期包含：ms-dotnettools.csdevkit, vue.volar, bradlc.vscode-tailwindcss
```

### 2. 架构红线自检确认
* [x] **浏览器内核版本**：必须锁定 `CefSharp.WinForms.NETCore` = `109.1.110` 与 `CefSharp.Common.NETCore` = `109.1.110`。
* [x] **原生宿主容器**：必须采用 `WinForms` (`System.Windows.Forms`)，严禁引入 WPF。
* [x] **.NET 目标框架**：必须锁定 `net6.0-windows`，并通过根目录 `global.json` 固定 SDK 行为。
* [x] **目标 CPU 架构**：显式配置 `<PlatformTarget>x64</PlatformTarget>` 与 `<Platforms>x64;x86</Platforms>`，严禁 `AnyCPU` 裸奔。
* [x] **前端编译目标**：`vite.config.ts` 中必须锁定 `build.target = 'chrome109'`。

---

## 二、 当期阶段目标（Stage 01 Objectives）

Stage 01 的核心目标是搭建**“可在 Win7 SP1 / Win10 / Win11 上稳定启动、支持双向异步 IPC 通信、具备 C 盘固定且可动态修改的持久化存储与分级日志体系”**的基础工程骨架：

1. **标准化工程目录与构建配置落盘**：
   * 建立 `HaloAgent.sln`、`global.json`、`src/Host.Desktop` 与 `src/Web.Client` 标准结构。
   * 配置 `.vscode/tasks.json` 与 `launch.json`，实现纯 VS Code 下的一键前端热更新调试与 C# 断点联调。
2. **Win7 防崩溃 CefSharp 宿主与自定义协议实现**：
   * 完成 TLS 1.2/1.3 兼容初始化、高分屏 DPI 自适应、Win7 (`NT 6.1`) 自动禁用 GPU 硬件加速、注册 `app://local` 本地安全协议替代 `file://`。
   * 实现开发态（`http://localhost:5173` + DevTools）与生产态（`app://local/index.html`）无缝切换。
3. **双向线程安全 IPC 通信架构**：
   * C# -> JS：封装 `JsEventDispatcher`（支持高频流式事件的 16ms 帧合并批量推送）。
   * JS -> C#：封装 `window.hostBridge` 异步调用门面，统一返回结构 `BridgeResponse<T>`，所有 UI 操作强制通过 `MainForm.Invoke` 切回主线程。
4. **C 盘固定默认存储路径 + 用户手动修改/迁移引擎（对应核心需求 #5）**：
   * 实现两级引导机制：固定引导文件 `%LocalAppData%\HaloAgent\bootstrap.json` 指向实际数据根目录。
   * 默认数据根目录固定为 `C:\HaloAgentData`（无根目录写权限时自动降级到 `C:\Users\<User>\AppData\Local\HaloAgent\Data`），并支持用户在 UI 中随时修改路径、校验权限及一键迁移历史数据与日志。
5. **分级滚动日志系统（App / LLM / Tool 三通道日志）**：
   * 在数据根目录 `logs/` 下按天写入结构化日志，支持前端实时查看日志目录、打开文件夹及清理过期日志。

---

## 三、 Stage 01 交付产物清单

| 模块分类 | 文件路径 | 核心职责说明 |
| :--- | :--- | :--- |
| **根工程配置** | `global.json` | 锁定 `.NET SDK 6.0.428` (`rollForward: latestFeature`) |
| **根工程配置** | `HaloAgent.sln` | 统一管理 `Host.Desktop` 项目 |
| **C# 宿主入口** | `src/Host.Desktop/Host.Desktop.csproj` | 锁定 `net6.0-windows`、`CefSharp 109.1.110`、`x64;x86` |
| **C# 宿主入口** | `src/Host.Desktop/Program.cs` | TLS 兜底、Storage 引导、Win7 GPU 防崩、Scheme 注册、CEF 生命周期 |
| **C# 宿主窗体** | `src/Host.Desktop/MainForm.cs` | 无冗余承载 `ChromiumWebBrowser`、注册 `hostBridge`、跨线程 UI 调度 |
| **C# 存储中枢** | `src/Host.Desktop/Infrastructure/Storage/StoragePathManager.cs` | `C:\HaloAgentData` 默认路径管理、`bootstrap.json` 读写、路径修改与数据迁移 |
| **C# 日志引擎** | `src/Host.Desktop/Infrastructure/Logging/HaloLogger.cs` | 线程安全 Channel 队列异步落盘日志（`app`/`llm`/`tool` 分类滚动） |
| **C# IPC 门面** | `src/Host.Desktop/Bridge/HostBridgeFacade.cs` | 暴露系统信息、存储路径读写、原生文件夹选择框、窗口控制接口 |
| **C# 事件推送** | `src/Host.Desktop/Bridge/JsEventDispatcher.cs` | 16ms 节流合并推送事件至前端 `window.__haloEventBus` |
| **前端脚手架** | `src/Web.Client/vite.config.ts` | 锁定 `target: 'chrome109'`，打包输出至 `../Host.Desktop/wwwroot` |
| **前端通信层** | `src/Web.Client/src/bridge/hostBridge.ts` | `CefSharp.BindObjectAsync("hostBridge")` 封装与开发态 Mock 降级 |
| **前端基础壳层** | `src/Web.Client/src/App.vue` | 应用主框架、顶部/底部状态栏、存储路径配置与连通验证面板 |

---

## 四、 本阶段文档导航

* 下一篇：[`02-cefsharp-win7-host-and-ipc.md`](./02-cefsharp-win7-host-and-ipc.md) —— 详细设计 WinForms + CefSharp 109 防崩宿主与双向 IPC 通信实现方案。
* 第三篇：[`03-storage-and-logging-engine.md`](./03-storage-and-logging-engine.md) —— 详细设计 C 盘固定路径引导、动态修改迁移与滚动日志引擎。
* 第四篇：[`04-frontend-scaffold-and-acceptance.md`](./04-frontend-scaffold-and-acceptance.md) —— 详细设计 Vite (`chrome109`) 前端脚手架、Win7 样式兼容规范与 Stage 01 验收标准。
