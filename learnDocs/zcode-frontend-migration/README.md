# 会话对话界面优化重构方案 — 总索引

> 输出位置：`docs/learnDocs/zcode-frontend-migration/`
> 基线代码：`develop/src/Web.Client`（`MessageTimeline.vue` / `MarkdownStream.vue` / `ToolCallCard.vue` / `FallbackCard.vue` / `stores/agentStore.ts` / `stores/projectionStore.ts` / `utils/turnGroup.ts` / `utils/toolCallRowToCard.ts`）+ `develop/src/Host.Desktop/Services/Projection`（`StreamToRowProjector.cs` / `RowProjectionStore.cs` / `ConversationDelta.cs` / `ConversationRow.cs`）+ `AgentOrchestrator.cs` 双通道投递。
> 设计输入：`docs/learnDocs/zcodeDocs/00~07`（双协议 → 流式归一 → 业务投影 → 前端存储/组装/渲染）。

## 目标（一句话）

把当前“持久靠 `messages.jsonl` + 流式靠 `agentStore` 瞬态桶 + 投影行只做对账”的三源并存，收敛为**以投影行（`ProjectionRow[]` + 封闭增量）为唯一真相源**的单向数据流；在此之上用**有序 `WorkBlock` 序列**正确表达 `正文 ⇄ 工具 ⇄ 思考` 的交错，用**可合并的工具组 + 可折叠的思维链**收纳信息密度，用**增量 Markdown + 虚拟化 + 引用共享**把流式帧预算压进 16ms。

## 非目标

- 不改双协议分发、`LlmGateway` 请求投影、模型网关鉴权与重试（`zcodeDocs/01~03` 已冻结）。
- 不改 `messages.jsonl` 落盘格式与历史迁移（只读兼容，见 `06`）。
- 不引入新 UI 框架、不换 Markdown 引擎选型（仍 `markdown-it`，只换用法）。

## 文档地图（建议按序阅读，施工可并行）

| 篇 | 文件 | 回答的问题 | 主要读者 |
|---|---|---|---|
| 00 | `00-现状诊断与问题清单.md` | 现有渲染链路到底烂在哪？每条症状的根因与证据（文件+行号） | 全员必读 |
| 01 | `01-信息模型-行与轮次与工作块.md` | 重构后的 canonical 类型是什么？`rowId/turnId/responseId` 不变量 | 前后端 |
| 02 | `02-思维链-Reasoning渲染规范.md` | 思考块何时出现、默认折叠/展开、流式、计数、复制 | 前端 |
| 03 | `03-工具调用-合并分组与卡片体系.md` | 连续工具如何合并？卡片三段式与 renderer 注册表 | 前端 |
| 04 | `04-正文Tool交错-收纳与轮次组装.md` | `正文+tool+正文+tool` 如何保序？折叠舱边界在哪？ | 前端 |
| 05 | `05-流式渲染性能-管线与虚拟化.md` | 每 token 只做多少工作？长会话为何不卡？ | 前端+后端 |
| 06 | `06-状态与数据源-单向数据流改造.md` | 如何杀掉三源并存？`rowId` 稳定、快照直供、乐观、重订阅 | 前后端 |
| 07 | `07-组件拆分与文件结构.md` | 拆成哪些文件？每个组件的 props/职责/hook 纪律 | 前端 |
| 08 | `08-后端投影补齐清单.md` | `StreamToRowProjector` 还缺哪些行/事件/档位？ | 后端 |
| 09 | `09-分阶段施工计划与验收.md` | 分几期做？每期改哪些文件？如何验收与回滚？ | PM+QA |

## 阅读路径

- 只想知道“改什么”：读本篇 + `00` 的 §7 决策表 + `09` 的任务表。
- 前端动手：`01 → 04 → 02 → 03 → 05 → 07`。
- 后端配合：`01 → 08 → 06`。
- 联调排错：先查 `00 §6 症状→根因速查`，再查对应篇的“验收/排错”节。

## 核心决策（先结论，后论证）

1. **唯一真相源 = 投影行**。`MessageTimeline` 不再读 `workspaceStore.sessionMessages`，只读 `projectionStore.rowsOf(sessionId)` 组装出的 `TurnUnit[]`。`sessionMessages` 降级为历史加载期的兼容镜像（见 `06`）。
2. **轮内保序用 `WorkBlock[]`**，不再用 `TurnGroup{content, reasoning, toolMessages}` 三桶。`WorkBlock = TextBlock | ReasoningBlock | ToolGroupBlock | SubagentBlock | MarkerBlock`，按 `rowId` 排序即渲染顺序（见 `01/04`）。
3. **思考默认收起、工具组默认收起、正文常显**。收起态只露出一行摘要（token 数/工具数/成功率/耗时），展开才挂载重型内容（见 `02/03`）。
4. **连续工具按 `(assistantResponseId + 相邻 + 时间窗)` 合并为 `ToolGroup`**，组头显示汇总，组内每卡仍可独立展开（见 `03`）。
5. **Markdown 全量 `md.render` 只发生在 `completed` 后一次；流式期用增量容错 + 高亮缓存 + rAF 节流**，删掉 `highlightAuto` 热路径（见 `05`）。
6. **`rowId` 必须后端分配、前端永不自增**。删掉 `projectionStore.snapshotFromMessages` 内的 `rowIdSeed` 自增，历史适配改用确定性派生（`hash(msgId)`，见 `06`）。
7. **后端补齐 `output.text` 流式与 `state.updated` 遥测/审批**，否则前端“终端直播”与“审批入口”永远对不上（见 `08`）。

## 预期收益（量化口径见 `09 §5`）

- 信息密度：工具 burst 10 连击从 10 张全展开卡 → 1 个组头 + 按需展开，首屏高度下降约 70%。
- 可读性：`正文→工具→正文` 不再被压成“一段正文 + 一舱工具”，因果链可逐段对照复制。
- 流式帧：`MarkdownStream` 单帧工作量从 `O(全文)` 降为 `O(增量)`，千 token 文本流式掉帧率显著下降；长会话（5000 行）滚动/输入保持 60fps。
- 正确性：断档、重订阅、后台切卡、`Continue` 续写、废弃 attempt 收口等 8 类竞态有确定语义，不再靠 `hiddenToolCallIds` 等 hacks 遮掩。

## 施工总约束（所有篇通用）

- `hook` 无条件执行，分发组件内不写条件 `hook`；`early return null` 只允许在所有 `hook` 之后。
- 行根节点必带 `data-row-id / data-kind`，工具行加带 `data-tool-call-id / data-status`，框选/埋点/右键菜单只认这组属性。
- `row.delta` 只追加文本，状态迁移只用 `row.upserted`；`row.removed(fromRowId)` 是唯一删除语义；`state.updated` 键级整体替换。
- 未知行 `kind` / 未知工具名永不白屏（`FallbackCard` + 未知 `op` 忽略）。
- 所有“是否可见/是否运行”由行字段决定，UI 不嗅探文本、不按时间推导（除过渡期 `isRunningWork` 兜底，且必须标注删除期限）。
