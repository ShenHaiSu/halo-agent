# need07 施工方案总览（对话历史右键：复制 · 重放 · 分支）

> 输出路径：`docs/need07/`；方案先行，代码按篇施工。
> 基线：`need06` 已落地时刻的代码（`MessageTimeline.vue` 时间轴直渲 + `turnGroup.buildTurnGroups` 轮次归并、`agentStore.sendPrompt/startAgentTurn` 单轮编排、`workspaceStore.sessionMessages/loadSessionMessages` 持久态、`hostBridge` 真后端 + Mock 双通道、`AgentOrchestrator.RunAgentTurnAsync` 先落 user 再循环、`SessionRepository.Backup/RewriteMessagesJsonlAsync` 原子重写、`CompactionService` 持追加锁互斥范式）。
> 目标读者：后端 + 前端施工同学；按本文顺序施工即合闸。

## 1. 一句话目标

在对话历史主界面（`MessageTimeline`）中，对**用户消息气泡**与 **LLM 返回轮次（含正文 + 思考 + 工具舱）**支持**右键上下文菜单**，提供**复制 / 重放 / 分支**三个动作，做到语义精确、可撤销可预期、与生成态互斥、双端（真后端 + Web Mock）一致。

## 2. 文档拆分（必读顺序）

| # | 文档 | 回答的问题 | 主要触点 |
|---|------|-----------|---------|
| 1 | `01-requirements-analysis.md` | 做什么 / 不做什么，三动作精确语义与边角 | 全局；需求冻结依据 |
| 2 | `02-backend-truncate-branch.md` | 后端怎么删、怎么分叉、怎么不丢数不互斥 | `WorkspaceSessionService`、`SessionRepository`、`HostBridgeFacade`、`AgentOrchestrator` |
| 3 | `03-frontend-context-menu.md` | 菜单长什么样、点下去发生什么、草稿箱怎么流转 | `MessageTimeline.vue`、`MessageContextMenu.vue`、`PromptComposer.vue`、`workspaceStore/agentStore/hostBridge` |
| 4 | `04-integration-test-plan.md` | 怎么证明做对了（单测 + 联调 + 合闸命令） | `tests/HaloAgent.Tests`、`vue-tsc/vite/dotnet`、手工矩阵 |

依赖关系：`01 → 02 → 03 → 04`。`01` 未冻结不得开工 `02/03`。

## 3. 范围一句话（详情见 01）

- **做**：user 气泡 + assistant 轮次右键菜单（复制原文 / 重放再生 / 分支新会话）；重放走“截断 + 复发”；分支走“前缀复制 + 新会话 + 草稿箱预填”；生成中全局禁用 destructive 动作；剪切板双通道（Clipboard API + execCommand 兜底）。
- **不做**：多选批量操作、system/tool 单条右键、分支跨工作区、重放保留被删内容的撤销栈（截断前仅写 `*.replay-bak-*.jsonl` 审计备份，不做 UI 撤销）、流式中热改历史。

## 4. 全局施工原则（四篇通用，冲突以各篇“精确到行”为准）

1. **零破坏只增量**：不动 `messages.jsonl` 行格式与 DTO 必填字段；只新增 Bridge 方法 + store 方法 + 一个菜单组件；`SystemPromptBuilder/TokenBudgetService` 不动。
2. **轮次原子**：任何截断/分支切面永不劈开 `assistant + 后续 tool*` 同轮；切面计算前后端共用同一“轮次展开”规则（见 01 §3、02 §3）。
3. **生成态互斥**：`isGenerating / streamingActive / compacting` 任一真时，重放/分支入口全部禁用，后端以 `SESSION_BUSY` 拒绝；复制永不禁用。
4. **失败不动盘**：anchor 不存在、上一条 user 不存在、空会话等全部前置校验失败直接返回，后端不写盘；重写一律 `Backup → Rewrite(tmp→move)`，与 `compact` 同范式。
5. **可测性**：新增 `data-testid="message-context-menu / context-menu-copy|replay|branch"`；后端 `dotnet build + dotnet test`，前端 `vue-tsc -b && vite build`；`04` 联调全表跑通方可合闸。

## 5. 强制施工顺序

```text
Step A 冻结 01（语义矩阵 + 边角表全员评审）
Step B 后端 02（Service 截断/分支 → Bridge 暴露 → 单测全绿）
  └─ 跑：dotnet build + dotnet test（新增 Stage07HistoryTests 全绿，且老用例不变）
Step C 前端 03（hostBridge + mock → store 编排 → Menu/Composer UI）
  └─ 跑：vue-tsc -b && vite build
Step D 联调 04（复制×2 × 重放×2 × 分支×2 × 生成态互斥 × 双端一致）
```

禁止 `01` 未评审就写 `02/03`；禁止单测未绿写前端；禁止跳过 `04` 合闸。

## 6. 验收总表（详情跳转各篇）

- [ ] 01：三动作语义矩阵全员一致；边角表（空/首条/末条/无上一 user/生成中）有明确预期。
- [ ] 02：截断/分支原子不断轮；`Σ账单`不清零；`MessageCount/UpdatedAt` 重算；`*.replay-bak / *.branch-src` 审计可查；忙态拒绝码统一。
- [ ] 03：右键定位准确、暗亮可读、无横溢；复制双通道成功；重放=截断后自动复发；分支=切卡 + 草稿箱预填（user 有草稿 / assistant 空）；生成中禁用态可见。
- [ ] 04：`dotnet + vue-tsc + vite` 全绿；手工矩阵 12 格全过；Mock 与真后端行为一致。
