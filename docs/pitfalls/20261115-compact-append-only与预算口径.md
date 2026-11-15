# P007：压缩 append-only 边界与预算口径踩坑记录

- 状态：**已闭环**（M5 落地：回填幂等 + 恢复工具 + 开关默认 stub）
- 日期：2026-11-15
- 范围：`messages.kind/meta_json`、`turns.kind`、活跃切片、预算基线

## 1. 为什么“边界必须结构化”（字符串桩 → 三处定义漂移）

- 现象：老式字符串桩靠 `content` 前缀 `【上下文摘要%` 识别，切片起点、前端分隔线、预算基线三处各自写了一遍前缀嗅探；M5 前后曾出现“读侧认得出、写侧认不出”。
- 根因：字符串约定没有唯一定义点，三处拷贝各自演进（全角括号、换行、截断后前缀丢失）。
- 教训：边界只能由结构化列定义——`messages.kind='compact_stub'`（`MessageKind` 唯一来源）、`turns.kind='compact'`（`TurnKind` 唯一来源）、`meta_json`（`CompactStubMeta` 唯一来源）。字符串前缀只允许出现在一次性运维回填的 `LIKE` 里（`SessionHistoryDb` 幂等 backfill），业务路径零嗅探。
- 红线：`ActiveContext.IsStub` 只认 `kind`；回填后老桩 `meta_json` 为空，前端走兼容层显示“上下文压缩”不带条数，不得反向补 `meta_json`。

## 2. 为什么“写入切 append-only 必须与读取切片同批”（M1/M2 原子交付对）

- 现象：M1 只把写入改成 append-only（压缩后旧行全留 + 桩），读取仍是全量 → 压缩前历史立刻重新进入模型上下文，预算与账单同时失真。
- 根因：写入与读取是同一语义的两面；只改一面，压缩在“账单上省了、在发包上没省”。
- 教训：M1（桩写入）与 M2（活跃切片）必须同一验收窗口合入（`04-§1` 原子交付对）；M1 开发期开关保持 `CompactMode=legacy`。`RemovedMessages` 语义统一为“被摘要覆盖的消息条数”，`KeptMessages` 在 stub 下等于全量条数。

## 3. 为什么超预算优先级的 `compact-estimate` 不能复用 `unknown`

- 现象：压缩成功后、下一轮 usage 到账前，占用快照为空；若复用 `unknown` 通道，前端会画 `?`，用户误以为预算丢失。
- 根因：`unknown` 表示“无快照”（空会话/极老数据），而压缩后是有明确估计值的（`max(512, summaryTokens + Estimate(桩后))`，短摘要按 `max` 口径，±10%）。
- 教训：新增独立基线 `OccupancyBaseline='compact-estimate'`（`08-§2` 状态机），顶栏三态 `exact / compact-estimate / unknown` 分开渲染；下一轮 `TurnCompleted` 回 `exact`。`meta.TotalInputTokens` 压缩时不置位（评估后不做，只读切片 + 桩基线修复口径）。

## 4. `turns.turn_seq UNIQUE` 与 `blocks.kind CHECK` 两个 schema 陷阱

- `turn_seq UNIQUE`：桩轮只能 `MAX(turn_seq)+1`，不插队、不重编号（`ResolveTurnSeq`）；多轮压缩自然叠加，切片只认最后一个桩。恢复工具删桩轮后**不重排**后续 `turn_seq`（本方案桩后无轮；若未来出现桩后轮，重排另案处理）。
- `blocks.kind CHECK`：`blocks.kind` 仅允许 `reasoning/text/tool_call/tool_result`（`SessionHistoryDb:47`），桩轮**零块**（`Blocks=0`、`FinalBlockId=NULL`，禁止走 `getBlockContents`）。新增桩语义只能落在 `messages.kind` / `turns.kind`，绝不动 `blocks`。
- 迁移幂等：`ALTER TABLE` 三段 + `ix_messages_kind` 部分索引 + 回填 `UPDATE` 全部可重入；`SQLite` 不做 `DROP COLUMN`，回滚口径是“开关回 `legacy` + 列保留”（有桩会话受冻结保护仍走 stub）。
