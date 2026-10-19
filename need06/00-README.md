# need06 施工方案总览（前置提示词可配化 · 设置与诊断中心新 Tab · 预发送预览）

> 输出路径：`docs/need06/`；只做方案，不改代码。
> 基线：`need05` 已落地时刻的代码（`SystemPromptBuilder.BuildAsync(workspace)` 硬编码模板、`AgentOrchestrator:153-228` 每轮强制前置拼接、`Chat/ResponsesAdapter.SerializeRequest` 双协议下发、`TokenBudgetService.HighWaterMark/LowWaterMark` 稳定前缀、`PrefixCachePredictor` 五块快照、`SettingsCenterDrawer` 五 Tab、`ProviderConfigService` 的 `config/*.json + AtomicFileWriter` 配置范式）。
> 目标读者：后端 + 前端施工同学；按本文顺序施工即合闸。

## 1. 一句话目标

把今天写死在 `SystemPromptBuilder.cs` 里的 Halo-Agent 系统前置提示词，拆成**可开关、可编辑、可预览、可诊断**的配置化模块，并在**设置与诊断中心新增一个 Tab「提示词与预发送」**，集中管理**所有预发送提示词**（主系统前置 + 压缩/标题等附属提示词至少可预览）。

## 2. 文档拆分（必读顺序）

| # | 文档 | 回答的问题 | 主要触点 |
|---|---|---|---|
| 1 | `01-requirements-analysis.md` | 做什么 / 不做什么，用户故事与验收口径 | 全局；需求冻结依据 |
| 2 | `02-prompt-model-and-config.md` | 配置长什么样、存哪、怎么迁移、怎么校验 | 新增 `Core/Models/PromptModels.cs`、`Infrastructure/Config/PromptConfigService.cs`、`config/prompt.json` |
| 3 | `03-builder-orchestrator-impl.md` | 后端怎么拼装、怎么生效、怎么预览、怎么不抖前缀 | `SystemPromptBuilder` 重构、`AgentOrchestrator:153-228`、`HostBridgeFacade.Prompt.cs`、`PrefixCachePredictor/TokenBudgetService` 联动 |
| 4 | `04-settings-tab-ui.md` | 新 Tab 长什么样、怎么编辑/预览/诊断 | `SettingsCenterDrawer.vue`、`PromptSettingsView.vue`、`stores/promptStore.ts`、`bridge/hostBridge.ts` |
| 5 | `05-integration-test-plan.md` | 怎么证明做对了（单测 + 联调 + 合闸命令） | `tests/HaloAgent.Tests/Stage06PromptTests.cs`、`vue-tsc/vite/dotnet` |

依赖关系：`01 → 02 → 03 → 04 → 05`。`02` 未冻结不得开工 `03/04`。

## 3. 范围一句话（详情见 01）

- **做**：6 块可开关系统前置（身份/环境/cmd-py 优先/工具指南/尾部易变/用户自定义）+ 全局 `prompt.json` + 新 Tab 编辑/预览/诊断 + 预览 API + 关断即零发送 + 双协议渲染差异说明 + token/前缀影响提示。
- **不做**：按工作区/会话/模型覆盖（本期预留字段不实现）、提示词版本历史（仅保留 `.bak` 一份）、流式中热切换本轮生效（本期下轮生效）、改压缩/标题提示词语义（仅预览）。

## 4. 全局施工原则（六篇通用，冲突以各篇"精确到行"为准）

1. **零破坏只增量**：默认配置渲染结果必须与当前硬编码模板**逐字节一致**（除用户改动外）；`SystemPromptBuilder.BuildAsync(workspace)` 老签名保留为兼容壳；不动 `messages.jsonl` 行格式与 DTO 必填字段。
2. **关断即零字节**：任何块关闭后，序列化输出中不得残留该块标题/空行/注释；`SystemInstructions` 为空时适配器不写 `system/instructions` 字段（现有 `IsNullOrWhiteSpace` 守卫保留）。
3. **稳定优先于省**：预览与实际拼装共用同一 `BuildCore` 路径；易变行永不进入 `stable` 段；开关变更视为有意的"前缀断裂"，需在诊断区显式提示命中率影响，不静默。
4. **失败回退默认**：配置损坏/缺失/超限时后端回退内存默认并在日志 + UI 横幅提示，绝不让 `RunAgentTurnAsync` 因提示词失败（现有 `SYSTEM_PROMPT_ERROR` 语义保留，仅默认回退不报错）。
5. **可测性**：`data-testid` 全新增（见 04 §6）；后端 `dotnet build + dotnet test`，前端 `vue-tsc -b && vite build`；`04` 联调必须跑 `05` 全表。

## 5. 强制施工顺序

```text
Step A 冻结 01+02（配置 JSON Schema 与默认值逐字节对齐现模板）
Step B 后端 03（PromptModels → PromptConfigService → Builder 重构 → Orchestrator 接入 → Bridge Preview API）
  └─ 跑：dotnet build + dotnet test（新增 Stage06PromptTests 全绿，且老用例不变）
Step C 前端 04（hostBridge + promptStore + PromptSettingsView + Drawer 第六 Tab + mock）
  └─ 跑：vue-tsc -b && vite build
Step D 联调 05（开关矩阵 × 双协议 × 预览一致性 × 前缀哈希 × 回退演练）
```

禁止 `02` 未评审就写 `03/04`；禁止单测未绿写前端；禁止跳过 `05` 合闸。

## 6. 验收总表（详情跳转各篇）

- [ ] 02：`prompt.json` 缺失/损坏/超限三态回退默认且有日志；默认渲染与老模板逐字节一致；`Save` 原子写盘。
- [ ] 03：总开关关 → `SystemInstructions=""` 且两适配器不下发 system 字段；`cmd-py` 块关 → 无 `CRITICAL RULE/cmd/python` 残留；预览 API 与实际发送同一代码路径；易变行仍在尾部。
- [ ] 04：第六 Tab 可用；6 开关 + 自定义编辑 + 工作区选择预览 + 双协议渲染对照 + token/前缀影响 + 上轮实际对照 + 一键恢复默认；1020px/94vw 无横向滚动。
- [ ] 05：开关矩阵 × 双协议全绿；连续 3 轮前缀哈希仅在配置变更轮变化；`dotnet + vue-tsc + vite` 全绿。
