# P0 验收纪要（自动化部分，2026-10-27，构建机 Win10 x64）

> 本文件记录本次 P0 落地中**已由机器验证**的结论。手工目视项（截图、跨机）
> 留待交叉验收时补齐，见 §3。

## 1. 一键构建

`powershell -File scripts/build-all.ps1`（默认 x86/Release）：EXIT=0，全程约 10 秒。

- [1/5] 契约生成：`未变化，跳过写入`（幂等 ✓）
- [2/5] 前端：`vue-tsc` 零报错 + `vite build` 零警告 + `[css-check] ✓ 1 个 CSS 文件全部兼容 Chrome 109`
- [3/5] C#：`dotnet build HaloAgent.sln -c Release -p:Platform=x86 -p:PlatformTarget=x86` 零警告零错误
- [4/5] 测试：15/15 通过（Contract 10、Server-stdio 3、Core 1、Storage 1）
- [5/5] 锁校验：10/10 通过；破坏演示（CefSharp→110.0.0）可被拦截并报中文原因

## 2. A 级指标（机器可验部分）

| # | 指标 | 结论 | 证据 |
|---|------|------|------|
| A-01 | TFM 与架构 | ✅ HaloAgent.exe / libcef.dll 均为 0x014C（x86） | A01-arch.txt |
| A-02 | CefSharp 109.1.110 / Chromium 109.0.5414.87 | ✅ | A02-*.txt |
| A-03 | Win7 SP1 启动 | n/a（按 P6 §14.1，新规则不建 Win7 虚拟机；Win7 相关代码保留） | — |
| A-04 | Win10 启动无黑屏 | ✅ 本机实测：窗口进程正常、12 秒后可关闭、无残留 | 日志（§4） |
| A-05 | 三方通道贯通 | ✅ 宿主日志 `引擎已就绪 pong` + `生产态加载 app://local/index.html`；stdio 自动化 3/3 | A07-server-tests.txt |
| A-06 | 产物兼容 Chrome 109 | ✅ `[css-check] ✓` | A06-css-check.txt |
| A-07 | 后端可独立运行 | ✅ `--stdio` 手工 + 3 自动化 + `HaloAgent.Cli -p system.ping` 均通 | A07 + 手工 |
| A-08 | 前端可独立运行 | ✅ `pnpm dev` HTTP 200，首屏 HTML 正确（控制台目视待补） | 手工 |
| A-09 | 契约单一真源 | ✅ 生成幂等 + 契约测试 10/10 | A09-*.txt |
| A-10 | 版本锁不可破 | ✅ 正常 10/10 通过；破坏 CefSharp 版本可拦截 | 手工演示 |
| A-11 | 关闭无残留 | ✅ `CloseMainWindow` 后 HaloAgent/Engine/BrowserSubprocess 全退出 | 手工脚本 |
| A-12 | 无未处理异常 | ✅ 本次发布构建运行全程日志 0 条 `[ERR]`（见 §4 说明） | 手工 |

## 3. 待人手工补齐（需显示器/另一台机器）

1. E-01/E-02/E-03 启动截图（页面、`--diagnostics` 输出、任务管理器）
2. E-09 三方通道贯穿截图（页面显示 `pong + 耗时`，点 10 次）
3. A-08 控制台截图（应只有一条 `[halo] 浏览器直开模式`，主题切换生效）
4. Win11 机器复跑 A-04（3 次启动）
5. B-03 复盘演练（3 个故意故障只看日志定位）
6. C 级性能基线填数（C-01~C-10）
7. A 级 12 条签字表、B 级检查表签字（见 05-验收方法.md §4/§5）

## 4. 本次发现并已修复的真实缺陷（均有回归覆盖）

1. **帧信封多出 `"type"` 字段**（违反 schema additionalProperties:false）：
   `abstract Type` + override 在 STJ 下产出 `type` 与 `t` 双字段。
   修：鉴别器 JsonIgnore，子类用 `T` 属性输出 `t`；Contract.Tests 守护。
2. **UTF-8 BOM 毒化 stdio 首帧**：.NET `StandardInputEncoding = Encoding.UTF8`
   经 StreamWriter 写出 EF BB BF 前导，引擎把坏行当 EOF 静默 0 退出。
   修：三处发送方改 `new UTF8Encoding(false)`；`HaloJson.ReadFrame` 容忍前导 BOM；
   `StdioChannel` 坏行只记 stderr 并继续读，不再当 EOF。
3. **async Main 导致 Cef.Shutdown 线程错位**：`Program.Main` 改回同步，
   `GetAwaiter().GetResult()`（注释说明无死锁原因）。
4. **宿主正常退出被记 `[ERR]`**：`EngineClient.Exited` 按 `_disposing`/退出码分级。
5. **app.manifest `manifestVersion="1.0.0.0"` 非法**：SideBySide 事件证实必须为 `"1.0"`，
   否则进程启动即报"并行配置不正确"。
6. **CefSharp 109 真实 API 与文档草案 5 处出入**：Register 无 isAsync 参数；
   IsBrowserInitializedChanged 事件参数为 EventArgs；FolderSchemeHandlerFactory
   在 `CefSharp.SchemeHandler` 命名空间且 5 参；JavascriptException 非 Exception；
   app.manifest 不得再声明 dpiAware（WFAC010）。
7. **postcss.config.ts 无法被 Vite 5 直接加载**（需 ts-node）：管线内联进
   vite.config.ts（数组形式），postcss.config.ts 保留为锁校验目标。
8. **postcss-preset-env v9 无 `container-queries` 特性键**：删除该行（Chrome 105+
   原生支持，无需降级）。
9. **`dotnet test` 在 x86 平台构建下用 x86 testhost**（本机无 x86 运行时）：
   sln 的 AnyCPU 映射改回 AnyCPU；build-all 的测试步骤与平台解耦。
10. **PS 5.1 `-File` 调用时 param 默认值中 `$PSScriptRoot` 为空**：
    两脚本改为正文推导；`.browserslistrc` 单行数组解包加 `@()`；ps1 转 BOM+CRLF。
11. **CefSharp 109 JS 桥保留 `Async` 后缀**（文档误写"去掉后缀"）：
    真机探针实测绑定键为 `getSystemInfoAsync,enginePingAsync,...`；
    前端调用侧已改，文档已勘误。
12. **CefSharp 109 默认不支持 Task 返回的桥方法**：
    必须在创建第一个浏览器前置 `CefSharpSettings.ConcurrentTaskExecution = true`，
    否则调用抛 "Your method returned a Task..."（探针复现 + 打开后调用成功）。
13. **CefSharp 109 返回 DTO 字段保持 PascalCase**（不做 camelCase）：
    前端按 C# 原名消费（`r.Ok/r.ElapsedMs`、`IsWin7`），见 `main.ts` 接口注释。

## 5. 与文档有意的偏离（均已在代码注释中说明）

- `Microsoft.Extensions.Logging.Console 6.0.0`：CPM 新增（文档只列到 Logging；
  不加则 `AddSimpleConsole` 编译失败）。
- `IsTrimmable` 从 Abstractions 移除（触发 IL2026，按 TreatWarningsAsErrors 即错）。
- `Engine.Cli` 发布 win-x86 自包含失败（NETSDK1031：Exe 项目被 Exe 引用）：
  Cli 仅用于开发机无头调试，不随包发布；P1 视情况抽公共库。
- vite `markdown` manualChunk 暂删（P0 无 markdown  import，空 chunk 告警；P4 加回）。
- `verify-locks`/`check-css-compat` 增强为 cwd 无关（默认路径按脚本位置解析）。
