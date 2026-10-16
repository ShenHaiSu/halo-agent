# need08 施工方案总览（智能体行为 Tab：命令超时 + 工具轮次可配）

> 输出路径：`docs/need08/`；方案先行，代码按篇施工。
> 基线：`need06` 已落地时刻的代码（`ExecCommandTool.DefaultTimeoutMs=30000/MaxTimeoutMs=300000 + Math.Min钳制 + CancelAfter + JobObject强杀`、`AgentOrchestrator.MaxIterations=25 + for(1..Max)+TurnTruncated`、`PromptConfigService(config/prompt.json+原子写+.bak+损坏回退)`、`SettingsCenterDrawer` 6 Tab、`ProviderManagerView` 预设按钮范式）。
> 目标读者：后端 + 前端施工同学；按本文顺序施工即合闸。

## 1. 一句话目标

 新增设置与诊断中心第 7 个 Tab（**智能体行为**，`name="agent"`，已决策冻结），让用户可配两个执行参数，并以后续杂项配置都能塞进此 Tab 而无需改名；后端同步实现 `0=无限` 语义：超时 `0=无限等待不杀进程`，轮次 `0=无限轮次不截断`；输入框旁放预设按钮简化操作；`reset` 按项/全部恢复（已决策），硬顶 1h/500轮（已决策确认）。

## 2. 文档拆分（必读顺序）

| # | 文档 | 回答的问题 | 主要触点 |
|---|------|-----------|---------|
| 1 | `01-requirements-analysis.md` | 做什么/不做什么；Tab 为什么叫这个名；`0=无限`精确语义；预设值表 | 全局；需求冻结依据 |
| 2 | `02-backend-agent-config-and-execution-semantics.md` | 后端怎么存、怎么校验、`0` 怎么跑不断、怎么不断轮/不丢取消 | `AgentConfigDocument/Service`、`ExecCommandTool`、`ToolContext/Registry`、`AgentOrchestrator`、`HostBridgeFacade` |
| 3 | `03-bridge-and-frontend-tab-ui.md` | Bridge 方法、store、前 Tab 长什么样、预设按钮怎么摆、脏保存怎么做 | `hostBridge.ts`、`*agentConfigStore*`、`SettingsCenterDrawer`、`AgentBehaviorView.vue` |
| 4 | `04-integration-test-plan.md` | 怎么证明做对了（单测+联调+合闸命令） | `tests/HaloAgent.Tests`、`vue-tsc/vite/dotnet`、手工矩阵 |

依赖关系：`01 → 02 → 03 → 04`。`01` 未冻结不得开工 `02/03`。

## 3. 范围一句话（详情见 01）

 - **做**：新 Tab 智能体行为 + `config/agent.json`（默认超时 30s/上限 300s/轮次 25）+ `0=无限`双链路 + 预设按钮 + 保存/按项恢复/全部恢复 + 损坏回退 + 下轮生效。
- **不做**：按工作区/会话/模型分套覆盖、导入导出、服务端下发、本轮热切换、历史版本时间线、改动网络超时与模型预算。

## 4. 全局施工原则（四篇通用，冲突以各篇“精确到行”为准）

1. **零破坏只增量**：不动 `messages.jsonl` 行格式、不动 `prompt.json/providers.json/network` 语义；`ExecCommandTool` 常量保留做回退默认值；`AgentOrchestrator(int maxIterations=25)` 签名保留做兼容。
 2. **下一轮生效**：配置保存后下一轮 `RunAgentTurnAsync` 起生效；流式中/执行中修改不中断本轮；本 Tab 无预览区，不做即时渲染。
3. **失败回默认不断会话**：文件缺失/为空/损坏/超限一律回退默认 + `fallbackReason` 横幅，下轮仍可正常发起；`Save` 一律 `Copy→.bak→原子写`。
4. **`0` 是显式危险操作**：前端必须有红色警告文案 + 二次确认（仅从有限切到 `0` 时弹一次，记住不再弹太多）；后端无限分支必须保留 `CancellationToken` 取消杀进程能力。
 5. **可测性**：新增 `data-testid="agent-behavior-tab / agent-timeout-*-input / agent-iterations-input / preset-* / agent-save / agent-reset-all / agent-reset-*"`；后端 `dotnet build + dotnet test`，前端 `vue-tsc -b && vite build`；`04` 全表跑通方可合闸。

## 5. 强制施工顺序

```text
Step A 冻结 01（命名 + 0语义矩阵 + 预设表 + 边角表全员评审）
Step B 后端 02（AgentConfig model/service → Exec 无限 → Orchestrator 无限 → Bridge 暴露 → 单测全绿）
  └─ 跑：dotnet build + dotnet test（新增 Stage08AgentConfigTests 全绿，且老用例不变）
Step C 前端 03（hostBridge + mock → store → Drawer 第7 Tab → AgentBehaviorView 预设 UI）
  └─ 跑：vue-tsc -b && vite build
Step D 联调 04（保存×恢复×损坏回退×无限×预设×下轮生效 × 双端一致）
```

禁止 `01` 未评审就写 `02/03`；禁止单测未绿写前端；禁止跳过 `04` 合闸。

## 6. 验收总表（详情跳转各篇）

 - [ ] 01：Tab 名已冻结智能体行为；`0` 语义矩阵无歧义；预设值/硬顶1h/500轮已确认；按项恢复 ID 表已冻结；边角表有明确预期。
 - [ ] 02：`agent.json` 原子写+`.bak`；校验矩阵全过；按项/全部恢复语义正确、未知项拒绝；`timeout=0` 真无限且可取消；`iterations=0` 真不截断且可取消；老常量路径单测仍绿。
 - [ ] 03：第 7 Tab 顺序/标题/无横溢；预设点击即填；有限→`0` 有确认+警告；脏检查+保存/按项恢复/全部恢复可用；Mock 与真后端一致。
 - [ ] 04：`dotnet + vue-tsc + vite` 全绿；手工矩阵（含 H10 按项/全部恢复）全过；存储迁移后配置不丢。
