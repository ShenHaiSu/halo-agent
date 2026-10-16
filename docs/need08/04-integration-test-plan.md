# need08-04 联调与测试计划（单测 + 联调 + 合闸）

> 前置：`01/02/03` 已落地。本篇是合闸总闸，不通过不得合并。

## 1. 后端单测（`tests/HaloAgent.Tests/Stage08AgentConfigTests.cs`，新建）

对标 `Stage06AgentOrchestratorTests(React_MaxIterations_Truncates)` + `Stage05ToolExecutionTests(AC03_TimeoutKillsTree)`，零修改老文件。

| 用例 | 输入 | 预期 |
|------|------|------|
 | `Defaults_CreateAndLoad` | 删除 `agent.json` 后 `EnsureInitialized+LoadWithMeta` | 三默认值 `30000/300000/25`，`isDefault=true` |
 | `Validation_NegativeRejected` | `D=-1/M=-5/N=-1` 分别 Save | 均 `AgentConfigValidationException`，field 分别为三字段 |
 | `Validation_CeilRejected` | `D=3600001/N=501` | 拒绝，field 正确（硬顶 1h/500 已确认） |
 | `Validation_DefaultGtMaxRejected` | `D=300000,M=60000` | 拒绝，msg 含 `默认超时不得大于上限` |
 | `Validation_ZeroAccepted` | `D=0/M=0/N=0` | 通过并落盘 |
 | `Reset_FieldMatrix` | 各改偏后 `ResetAgentConfig("defaultTimeout"/"maxTimeout"/"iterations")` | 仅该项回默认（30000/300000/25），另两项保持改偏值 |
 | `Reset_AllAndUnknown` | `ResetAgentConfig(null/"all")` 全回；`ResetAgentConfig("bogus")` | 全回三默认；未知项抛 `field` 异常 |
 | `Resolve_TimeoutMatrix` | 01 §4.1 表格每一行（缺省/负/`T=0`/`M=0`/钳制/`D=0`缺省） | `E` 与表一致（纯函数单测 `AgentTimeoutResolver.Resolve`，推荐从 Tool 内抽纯函数以便测） |
 | `Corrupt_FallsBack` | 写垃圾字符串到 `agent.json` | `LoadWithMeta` 回默认 + `fallbackReason` 非空 + `corrupt-*` 文件存在 |
 | `Save_AtomicBak` | Save 两次不同值 | 第二次后 `.bak` 内容等于第一次值 |
 | `Exec_Infinite_NoKill` | `T=0` 跑 `ping -n 6`（~5s） | 正常完成非超时，`Success=true` 且无 `命令执行超时` 字样 |
 | `Exec_Infinite_Cancellable` | `T=0` 跑 `ping -n 60`，1s 后 cancel | 1~2s 内返回取消，进程树无残留（沿用 AC03 断言范式） |
 | `Iter_Infinite_NoTruncate` | `N=0` + mock gateway 永远返回 1 个 toolcall，跑 5 轮后 cancel | 前 5 轮无 `TurnTruncated`，`Cancelled(5,…)` |
 | `Iter_Finite_Truncates` | `N=3` + 同上 mock | 第 3 轮 `Truncated(3,…)` + 助理通知落盘（沿用 `React_MaxIterations_Truncates` 断言） |

命令：`dotnet build HaloAgent.sln` + `dotnet test tests/HaloAgent.Tests --filter Stage08` 全绿，且全量 `dotnet test` 老用例零失败。

## 2. 前端检查

| 项 | 命令/操作 | 预期 |
|----|-----------|------|
 | 类型+构建 | `vue-tsc -b && vite build`（web 目录） | 零错误 |
 | Tab 挂载 | 打开设置中心 → 第 7 Tab `智能体行为` | 标题/顺序正确，1020px 与 94vw 无横向滚动 |
 | 预设 | 三组预设逐个点击 | 输入即填、高亮切换正确、`∞` 红色描边 |
 | 无限确认 | 有限→`∞`（输入+预设两路） | 各弹一次确认；取消回滚；`0→有限`不弹 |
 | 警告 | 任一 `0` | 对应红色 `n-alert` 可见；`D>M>0` 黄色提示 + 保存禁用 |
 | 保存流 | 改值→保存→重进 | `下一轮生效` toast；重进值保持；`fallbackReason` 横幅按需显示 |
 | 恢复 | 行内按项恢复→确认（只回该项）+ 全部恢复→确认（三值回 `30000/300000/25`） | 均成功 toast；未保存编辑按 03 I8 规则处理 |
| Mock 一致 | `npm run dev` mock 态全流程 | 与真后端同文案同 `data-testid` |

## 3. 手工联调矩阵（真后端，12 格，必填表）

| # | 场景 | 步骤 | 预期 |
|---|------|------|------|
| H1 | 改默认超时 | `D=60000` 保存 → 发 `timeout_ms` 缺省的 `dir` | 60s 内不杀；`tool 日志` 无超时 |
| H2 | 上限钳制 | `M=60000` 保存 → 发 `timeout_ms=300000` 的 `ping -n 90` | ~60s 超时提示（含 `60000ms`）且杀树 |
| H3 | 单 call 无限 | 任意 `D/M` → 发 `timeout_ms=0` 的 `ping -n 10` | 跑完不杀；取消按钮 1s 内可终止 |
| H4 | 默认无限 | `D=0` 保存 → 发缺省 `ping -n 10` | 跑完不杀 + B 区红警常驻 |
| H5 | 上限不限 | `M=0` 保存 → 发 `timeout_ms=600000` 的 `ping -n 20` | 不被钳到 300s，按 call 值跑 |
| H6 | 轮次调小 | `N=3` 保存 → 发需多步任务 | 第 3 轮截断通知 + `TurnTruncated` |
| H7 | 轮次无限 | `N=0` 保存（二次确认）→ 发多步任务 | 无截断持续跑；点取消即停 |
| H8 | 下轮生效 | 进行中改 `N` 并保存 | 本轮不受影响，下一轮新值生效 |
| H9 | 损坏回退 | 手改 `agent.json` 为垃圾 → 重进 Tab + 发一轮 | 横幅显示损坏回退，会话正常 |
 | H10 | 按项/全部恢复 | 改偏三项→行内恢复轮次→确认；再点全部恢复→确认 | 前者仅轮次回25另两项保持；后者三值回默认，下一轮按默认跑 |
| H11 | 存储迁移 | 切换 DataRoot（迁移数据）→ 重进 Tab | 配置值跟随迁移不丢 |
| H12 | 双端一致 | 同 H3/H7 在 dev-mock 重做 | 文案/`∞` 渲染一致（功能示意即可） |

## 4. 合闸命令（按序，任一红即停）

```text
1) dotnet build HaloAgent.sln
2) dotnet test tests/HaloAgent.Tests（全量，老+Stage08 全绿）
3) web 侧 vue-tsc -b && vite build
4) H1-H12 手工矩阵全过（截图/录屏留档至少 H3/H4/H7）
5) git status 确认仅 need08 相关文件 + 代码增量，无 prompt/network 无关改动
```

## 5. 回滚与风险

- 后端回滚：删 `agent.json` 即回默认；代码回滚保留旧常量/旧构造值，会话不受影响。
- 最大风险是 **`0` 常驻**：用户开无限后忘记关。缓解：Tab 常驻红警 + 保存日志 Warn + 文档首屏声明；本期不做自动过期（如需，后续杂项卡片再加“无限自动回退”开关，不在本期）。
