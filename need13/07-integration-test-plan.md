# need13-07 联调与测试计划（单测 + 前端 + 手工矩阵 + 合闸）

> 前置：`01/02/03/04/05/06` 已落地。本篇是合闸总闸，不通过不得合并。

## 1. 后端单测（新建 `tests/HaloAgent.Tests/Stage13SubAgentTests.cs` + `Stage13SubAgentConfigTests.cs`，零修改老文件）

对标 `Stage08AgentConfigTests` + `Stage06AgentOrchestratorTests`，`ScriptedFakeGateway` 复用 08 范式。

### 1.1 配置（`Stage13SubAgentConfigTests`）

| 用例 | 输入 | 预期 |
|---|---|---|
| `SubDefaults_OldFileCompatible` | 写无 7 字段的老 `agent.json` 后 `LoadWithMeta` | 7 默认（true/3/15/true/true/false/false/false），`isDefault=false`，无 fallback |
| `SubValidation_ParallelRejected` | `subMaxParallel=0/9` Save | 均 `AgentConfigValidationException`，field=`subMaxParallel` |
| `SubValidation_IterationsRejected` | `subMaxIterations=0/101` Save | field=`subMaxIterations` |
| `SubAsk_LockedFalse` | `subAllowAskUser=true` Save→Load | 读回 false（归一不断会话） |
| `SubReset_Matrix` | 改偏后 `ResetAgentConfig(subEnabled/subParallel/subIterations/subPerms)` | 仅该组回默认，另组保持 |
| `SubReset_AllAndUnknown` | `Reset(null/"all")` 全回；`Reset("bogus")` | 10 字段全默认；未知抛 field 异常 |
| `SubCorrupt_FallsBack` | 写垃圾到 `agent.json` | 回默认 + `fallbackReason` 非空 + `corrupt-*` 存在 |

### 1.2 权限与拒绝（`Stage13SubAgentTests` 权限组）

| 用例 | 输入 | 预期 |
|---|---|---|
| `Sub_SkillWriteDenied` | sub ctx 调 `skill_tool{action:create}` | `Fail` 含 `[PERMISSION_DENIED:skill_tool:write]` |
| `Sub_SkillReadAllowed` | sub ctx 调 `skill_tool{action:list}` | 成功 |
| `Sub_MemoryWriteDenied` | sub ctx 调 `memory_tool{action:delete}` | `Fail` 含 `[PERMISSION_DENIED:memory_tool:write]` |
| `Sub_AskDenied` | sub ctx 调 `ask_user` | `Fail` 含 `[SUBAGENT_ASK_DENIED]`，`HasPending==false` |
| `Sub_NestedSpawnDenied` | sub registry 调 `spawn_subagent` | `Fail` 含 `[SUBAGENT_NESTED_DENIED]` |
| `Main_SpawnDisabled_NoSession` | `subAgentEnabled=false` 快照下主调 spawn | `Fail` 含 `[SUBAGENT_DISABLED]` 且 `GetSubSessionsAsync` 为空 |

### 1.3 编排（`Stage13SubAgentTests` 编排组，mock gateway）

| 用例 | 输入 | 预期 |
|---|---|---|
| `Barrier_CollectsAll` | 主首轮回 3 spawn；子各 2 轮 tool 后纯文本总结 | 主进 iteration2 时历史含 3 条 `Tool(spawn)` 回包（3 success），`SubAgentsBarrierReleased` 发布 |
| `Quota_PartialReject` | `subMaxParallel=2` + 主首轮 4 spawn | 2 建子跑，2 当场 `[SUBAGENT_QUOTA_EXCEEDED]`；主收齐 4 回包 |
| `CancelTree_StopsAll` | 3 子长 tool 中取消主 | 主子全 `Cancelled`，`_trees` 清空 |
| `SubTruncated_Bubbled` | 子 `maxIterations=1` 仍发 toolcall | 子 `Truncated` → 回包 `success=false,errorCode=TURN_TRUNCATED`，主下一轮可见 |
| `Heal_WithItemId` | 含 `ItemId` 的孤儿 history 调 `Heal` | 通过（回归双协议 `ItemId` 落盘兼容） |
| `RespChat_MixedTree` | 主 `resp` 模型 + 子覆盖 `chat` 模型各 1 轮 tool | 双边 `TurnCompleted`，用量分桶 `session` 正确 |

命令：`dotnet build HaloAgent.sln` + `dotnet test tests/HaloAgent.Tests --filter Stage13` 全绿，且全量 `dotnet test` 老用例零失败。

## 2. 前端检查

| 项 | 命令/操作 | 预期 |
|---|---|---|
| 类型+构建 | `vue-tsc -b && vite build`（web 目录） | 零错误 |
| D/E 卡挂载 | 设置中心 → `智能体行为` → 滚到 D/E | 标题/顺序/控件正确，1020px 与 94vw 无横向滚动 |
| 预设 | 两组预设逐个点击 | 即填高亮对 |
| 危险确认 | 关→开 skill 写 | 弹确认，取消回滚；`ask` 置灰不可点 |
| 保存流 | 改值→保存→重进 | `下一轮生效` toast；重进保持；`fallbackReason` 按需横幅 |
| 恢复 | 4 组按项恢复 + 全部恢复 | 只回该组/全回 10 字段，均 toast |
| 等待态 | mock 主 spawn 2 子未回 | 主输入禁用 + `取消可掐断整树`；`Released` 后恢复 |
| Mock 一致 | dev-mock 全流程 | 同文案同 `data-testid` |

## 3. 手工联调矩阵（真后端，16 格，必填表）

| # | 场景 | 步骤 | 预期 |
|---|---|---|---|
| H1 | 总开关关 | 关→保存→主发“并行查两目录” | 主不 spawn（prompt 预声明生效）或调了也被拒；拒因转述含“设置中重新开启” |
| H2 | 总开关开 3 并发 | 开→发 3 并行子任务 | 3 子全跑，barrier 后主汇总含 3 结论 |
| H3 | 超限 | `max=2`→发 4 spawn | 2 跑 2 拒，主收齐 4 回包后继续 |
| H4 | 子轮次 | 子 `maxIterations=2`→发需 5 步子任务 | 子 `Truncated` 回填，主拆小重 spawn 或串行收尾 |
| H5 | 子只读 | 关 file 写→子任务需写文件 | 子写拒，总结注明，主代写 |
| H6 | 子禁问 | 子任务含歧义需确认 | 子按默认继续 + 总结注明假设，主转交用户确认 |
| H7 | 套娃 | 诱导子再 spawn | 子拒并串行完成 |
| H8 | 取消整树 | 3 子运行中点取消 | 主子全停，主 `TurnCancelled`，无残缺回填 |
| H9 | 单掐子 | 诊断卡掐 1 子 | 该子 cancelled，主继续等余子（含已取消子的 cancelled 结果） |
| H10 | 下轮生效 | barrier 等待中改 `max=1` 保存 | 本轮 3 子继续，新轮 spawn 按 1 判 |
| H11 | 损坏回退 | 手改 `agent.json` 为垃圾→重进 Tab+发一轮 | 横幅回退，主子均可正常跑 |
| H12 | 按项/全部恢复 | 改偏→行内恢复权限→确认；再全部恢复 | 前者仅权限回默认；后者 10 字段回默认 |
| H13 | 混协议 | 主 `resp` 模型 + 子覆盖 `chat` | 双边完成，用量分桶正确 |
| H14 | 删除级联 | 删有子运行的主会话 | 先取消后删盘，子盘无残留 |
| H15 | 重启自愈 | 子运行中杀进程重启 | 子置 cancelled + 关闭 notice |
| H16 | 双端一致 | H2/H8 在 dev-mock 重做 | 文案/`data-testid` 一致 |

## 4. 合闸命令（按序，任一红即停）

```text
1) dotnet build HaloAgent.sln
2) dotnet test tests/HaloAgent.Tests（全量，老+Stage13 全绿）
3) web 侧 vue-tsc -b && vite build
4) H1-H16 手工矩阵全过（截图/录屏留档至少 H2/H3/H8）
5) git status 确认仅 need13 相关文件 + 代码增量，无 prompt/network 无关改动
```

## 5. 回滚与风险

- 后端回滚：删 `agent.json` 即回默认（含子全默认开 3/15）；代码回滚删 `SubAgentCoordinator/SpawnTool/Factory` 引用，主 registry 不注册 spawn 即回单代理，会话不受影响（子会话残留为只读历史，可手动删）。
- 最大风险是 **barrier 常驻等待**：子 tool 死循环 + 主无限等待。缓解：子默认 15 轮截断；诊断卡常驻运行表 + `掐断整树`；E 卡拒绝横幅；文档首屏声明。v1 不做 barrier 超时（如需，后续加 `subBarrierTimeoutMin` 卡片，不在本期）。
- 次风险是 **token 放大**：3 子 × 15 轮。缓解：`max=3` 默认小；`TurnCompleted.payload.subAgents` 透出子耗；用量 Tab 可选 rollup。
