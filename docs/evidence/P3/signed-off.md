# P3 签署表（自动化部分）

> 判定原则：A 级全过才允许进入 P4。以下为机器可验部分结论；
> 人工/真机项（窗口/托盘/Win7）待整机联调补签。

| # | 验收项 | 结论 | 证据文件 | 日期 |
| :--- | :--- | :--- | :--- | :--- |
| R-2 | 无残留进程（代码路径） | ✅ 受控关闭序列已实现（待 10 次启停实测） | B-checks.txt | 2026-10-27 |
| R-3 | 无 `file://` 生产入口 | ✅ C# 零命中；入口 `app://local/index.html` | A-10.4-no-file-protocol.txt | 2026-10-27 |
| R-4 | 桥接方法数 = 11 + 3 | ✅ 代码级精确（待诊断页复测） | A-9.3-router-tests.txt | 2026-10-27 |
| A-1.2 | `--diagnostics` 完整 | ✅ 7 分节 + 落盘 + 退出码 0 | A-1.2-diagnostics-win10x64.txt | 2026-10-27 |
| A-1.3 | 依赖缺失可操作提示 | ✅ 退出码 4/5 路径实测（x86 VC++ 为本机环境缺失） | AppLog 片段见 INDEX | 2026-10-27 |
| A-1.4 | 12 参数全支持 | ✅ 单元 + 真机矩阵 | A-1.4-*.txt | 2026-10-27 |
| A-1.5 | 发布版拒绝 `--dev` | ✅ 退出码 2 | A-1.5-nodev.txt | 2026-10-27 |
| A-2.1 | GPU 判定矩阵 | ✅ 31/31（含矩阵 11 + 虚拟机 + null 探测） | A-2.1-gpu-matrix.txt | 2026-10-27 |
| A-2.2 | 驱动黑名单 ≥8 | ✅ 9 关键字 + 来源注释 | A-2.2-blacklist.txt | 2026-10-27 |
| A-2.4 | 不加 rasterizer | ✅ 仅注释占位 | A-2.4-no-rasterizer.txt | 2026-10-27 |
| A-3.4 | MIME 表 ≥18 | ✅ 19 种 + charset | A-3.4-mime.txt | 2026-10-27 |
| A-9.3 | Router 与 schema 一致 | ✅ 4/4 相等 | A-9.3-router.txt | 2026-10-27 |
| B-01~B-20 | B 级静态检查 | ✅（B-14 484 行达标） | B-checks.txt | 2026-10-27 |
| A-16 | 全解回归 293/293 | ✅ 无 P1/P2 回归 | A-16-full-sln.txt | 2026-10-27 |
| R-1/A-2.5/W | Win7 真机 | ⬜ 待 P6 X-13（本机 Win10 + `--force-software` 代测留待联调） | — | — |
| A-4/5/6/7/8/10 | 整机联调 | ⬜ 待人工（诊断页/启停/托盘/压测） | — | — |
| C | 性能基线 | ⬜ 待填数 | — | — |

## 与阶段文档有意的偏离（均已记录）

- 契约 54→58：新增 4 个 `implementedIn=host` 的 fs 对话框方法 + 全员 `implementedIn`
  元数据；`contractVersion` 保持 1（非破坏性，见 contract/CHANGELOG.md）。
  `KnownMethods` 双份清单（引擎 + Contract.Tests）已同步。
- `window.haloHost` 另有 P0 兼容桥 `hostBridge` 并存：不断现有前端 Transport，
  R-4 仅约束 haloHost/haloAgent 精确计数。
- `EngineSupervisor` 保留 P1 兼容面（`State/TryGetClient/RetryInBackground`）供旧桥接；
  P3 新面（5 状态/3 次重启/1s2s4s/30s 超时/心跳）为默认路径。
- `MultiFileDropTarget`（RevokeDragDrop）未做：109 的 `IDragData` 仅单文件名，
  P3 用 `IDragHandler` 单文件拖放 + 前端 `<input type=file multiple>` 降级（A-3.6 已在诊断页覆盖）。
- MainForm 484 行内含 P0 兼容桥接字段；`update.*` 仍由引擎 stub 占位（P6 实现）。
