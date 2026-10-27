# P3 验收证据索引

生成时间：2026-10-27（构建机 Win10 x64 22H2）
Git 分支：v2-dev（P3 宿主与 CefSharp 落地）

## 自动化结论（本机实测，`scripts/verify-p3.ps1` 全绿）

- A-01a Release 构建：全解零警告零错误（`-warnaserror`）
- A-01b 依赖纯净：`dotnet list package --include-transitive` 无新增第三方
  （CefSharp 109.1.110 锁定；日志仅用 `Microsoft.Extensions.Logging.Abstractions`）
- A-1.4 命令行：12 参数 + 环境变量兜底 + 非法拒绝
  - `--data-dir X` / `HALO_DATA_DIR` / 参数优先于环境变量（见 A-1.4-*.txt）
  - `--unknown-flag` / `--dev-url` 缺值 → 退出码 2
- A-1.5 发布版拒绝 `--dev`：退出码 2（A-1.5-nodev.txt）
- A-2.1 GPU 五级判定：Host.Tests 矩阵全绿（含 Win7/崩溃/黑名单/虚拟机/配置优先）
- A-2.2 驱动黑名单：9 关键字全命中且逐行有来源注释
- A-2.4 `disable-software-rasterizer` 默认不加（仅注释占位）
- A-3.4 MIME 表：19 种扩展名 + 文本类全带 `charset=utf-8` + `.js` 精确 MIME
- A-9.3 Router 与 schema 一致：`HostMethodRouter` == schema `implementedIn=host`
 （fs.browse / fs.pickFiles / fs.pickSavePath / fs.readImageAsBase64）
- A-10.4 无 `file://` 生产入口：C# 零命中；生产态入口 `app://local/index.html`
- B 级：无 `async void`；ChromeForm 169 行；MainForm 484 行；
  manifest 4 个 supportedOS；硬编码绝对路径 0 处
- A-16 全解回归：Contract 17 + Host 31 + Storage 19 + Server 33 + Core 193 = 293/293

## 真机实测（x64 自包含发布 `publish/p3-verify-x64`，本机）

- A-1.2 `--diagnostics`：7 分节完整输出并落盘，退出码 0
  （程序/系统/浏览器内核/Chromium 109.0.5414.87/CefSharp 109.1.110/
  显示 GPU/依赖/路径/上次运行；libcef 行含架构与大小；GPU 决策非空）
- A-1.3 依赖缺失：
  - AnyCPU 构建目录缺 libcef.dll → 退出码 4（中文对话框，日志有中文原因）
  - x86 发布在本机缺 x86 版 `vcruntime140_1.dll` → 正确报“缺少运行库”（环境缺失，非代码问题）
- A-2.3 崩溃降级：逻辑由 `CrashMarker.CheckAndRead → GpuPolicy.Decide(lastRunCrashed)` 覆盖；
  人工写 marker 验证留待整机联调
- 前端构建：`vue-tsc` 零报错 + `vite build` + `[css-check] ✓ Chrome 109 兼容`

## 文件清单

- A-01a-build.txt / A-01b-packages.txt
- A-1.2-diagnostics-win10x64.txt（x64 发布实测输出全文）
- A-1.4-cli.txt（Host.Tests 命令行）/ A-1.4-cli-matrix.txt（退出码矩阵）
- A-1.4-datadir.txt / A-1.4-env.txt / A-1.4-override.txt / A-1.4-reset.txt
- A-1.4-badflag.txt / A-1.4-novalue.txt / A-1.5-nodev.txt（空输出+退出码见 matrix）
- A-2.1-gpu-matrix.txt / A-2.2-blacklist.txt / A-2.4-no-rasterizer.txt
- A-3.4-mime.txt / A-9.3-router.txt / A-9.3-router-tests.txt
- A-10.4-no-file-protocol.txt / A-16-contract.txt / A-16-full-sln.txt
- B-checks.txt

## 手工项（待整机联调，见 06-验收方法.md）

- A-1.1 13 步启动日志完整 markers（含引擎 Ready + 关闭序列完成）
- A-2.5 `--force-software` 连续 5 次可见界面（Win7 真机按 P6 记 n/a）
- A-3.x 诊断页（`app://local/__diag.html` → 全部运行 → diag-result.txt）
- A-4.x 引擎进程（就绪耗时/优雅关闭/心跳挂起/重启上限/孤儿/Job）
- A-5.x 窗口（拖拽 9 向/贴边/最大化留边 4 方向/位置持久化/DPI）
- A-6.x 系统集成（单实例连点/托盘 10 项/热键/三选一/拖放/关机快退）
- A-7.x 桥接（14 方法全可用/压测/事件补推/openExternal 正例）
- A-8.x 崩溃恢复（渲染崩溃 3 次上限/宿主 marker/异常兜底）
- A-9.1 58 方法遍历（update.* 返回占位，fs.* 由宿主处理）
- A-10.x 10 次启停无残留/句柄不累积/缓存可删除
- C-01~C-18 性能基线填数 / W-01~W-22 Win7 专项 + GPU 组合实测表
