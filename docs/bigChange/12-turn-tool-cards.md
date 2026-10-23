# 12 Turn + 工具卡状态与动作卡（P2-6）

第二参考：`uiuxAndStyle/05-turn-cards.md`。前置：11 disclosure 已合入。

## 1. 前置检查

- [ ] 通读 `TurnGroupItem.vue + ToolCallCard.vue + useTimeline.ts`：列出当前 turn 头（telemetry/protocol/cache）、工具 head/body、错误/重试现状。
- [ ] 确认缺失：`PermissionCard/AskToolCard/PlanApprovalBar/ReviewChangeCard` 是否存在（大概率无）——本阶段新建。
- [ ] 确认 Bash 工具 `runOutcome/exit` 是否回传到前端（决定 head 状态优先级能否做）。

## 2. 阶段目标

- Turn 四态 + 运行文案 + 错误卡动作；失败只出 `TurnOutcomeCard`。
- 工具 head 运行/完成两套文案 + 状态 pill + chips；body 按块懒建。
- 动作卡全部 live-only（挂 composer dock，不进 transcript）；readingWindow 全禁。

## 3. 实现办法

### 3.1 Turn `TurnProcess`（改 `TurnGroupItem`）

- 相位机：`failed（本轮自有失败）> stopped > running{compacting/recovering/retrying/waiting-subagents} > completed`。
- 头文案：失败/停止/完成（+`For` 时长，`startedAt` 已知且 >0s）；运行走 compacting/recovering/retrying/waiting-subagents，否则 Thinking/running；issues>0 非失败出 error 数徽；工具数徽；active 加 `active` 类 + spinner + 秒级 timer。
- flags：`complete=!isActive && !hasError && hasContent && actionMessage`；`streaming=isActive && summary.streaming` 行挂 `streaming` 类。
- 错误卡：未 dismiss 才显；`errors.*`（cert 变体）+ code/networkCode + details disclosure 默认开；动作 Continue（发 `continue` 语义 prompt）/ Open settings（仅配置错）/ dismiss / 复制错误详情。

### 3.2 TurnOutcomeCard（新建，仅失败）

- 条件：`result && status!==completed && 无内联错误已示 && tail 非空`；单 `failed` 样式 `role=status`。
- 文案：`需要注意/失败正文/步骤数`；唯一动作“继续”→ 发 `continueUnfinishedTask`；readingWindow 内不渲染。

### 3.3 工具行（改 `ToolCallCard`）

- 状态：`message.toolStatus（running/error/denied/success）`；Bash 以 shell exit 覆盖调用状态；`failed=error||run===failed`。
- head 文案：运行 `reading/listing/searching/writing/editing/running/fetching/using/delegating/updating`；完成 `read/listed/...`；Task 拓扑走 `subagentStatus.*` 或花名册汇总，spawn 中 `creating`；run-head `运行中/失败/拒绝/完成`。
- head 右：chips 常显；Bash head 钉状态 pill + `role=status` + 命令 + 复制 + outcome（体只放输出）；caret 仅指针区可点。
- body：`ToolDetailBlocks plain/streaming`；块 `content/written/command/stdout/stderr/diff/files/matches/details/notice/error/output/input` 各带本地化头 + 逐块复制（code/diff/paths/`path:line: text`/字段/note）；溢出 `more{count}`；chips 行 `exit/matches/files/replacements/truncated/scratch/lines/size`；Ask 历史 Q/A 含 skipped；文件/命中块 workspace 相对路径变预览按钮+菜单，其余纯文本。

### 3.4 动作卡（新建，全部 live-only）

- Permission：`risk-low|med|high` + 排队数 + 子代理来源 + prompt+reason+结构化 args + workspace 行；动作 拒绝（ghost）/ 允许本会话（次）/ 允许一次（主）；resolving 全禁；错 toast；焦点回 composer。
- AskTool：多问向导，未答/已答/跳过点可点，进度 `current/total + queued`；单/多选 + 自定义 + 文本；Next 空跳、Skip 清空、末 Next 提交 `answers[][]|null`；头 拒绝（全 null）/ Skip / Next|Submit；resolving 全禁。
- PlanApproval：plan|goal 共 bar；pending 才出动作行：拒绝 + 分体批准（主 + 模式菜单 ask/acceptEdits/auto）；批准持久化模式；busy 禁 + `aria-busy`；有工件开 work-panel。只挂 composer dock（`planCheckpoint.status===pending`），AskTool 同理——永不进 transcript。
- ReviewChange：合头（caret + A/M/D + 路径 + +add/−del + 可访问全标签 incl. rolledBack）；展开 hunk diff 或 binary/tooLarge/noLineDetails；动作 回滚（可逆+本地工具允许，busy `rollingBack`）/ 已回滚 check / 不可回滚 note + 复制 diff（空禁）。

## 4. 验收标准

- [ ] Turn 四态快照；运行文案随 phase 切；错误卡动作可用。
- [ ] 工具 head 两套文案 + Bash exit 覆盖；body 块懒建（合起不建块）。
- [ ] 动作卡只在 live dock 出现，历史窗/ transcript 内无；resolving 禁用 + toast + 焦点回归。
- [ ] 无障碍：pill `role=status`，busy `aria-busy`，失败 `role=status`。
- [ ] 旧 `ToolCallCard` 单一样式/全量 body 渲染删除（破坏性）。

## 5. 风险

- 后端若无 `runOutcome/queued/planCheckpoint` 事件，需补 bridge 事件；允许加事件，不兼容旧 payload。
