# 11 搜索 + ReadingWindow 降级 + Disclosure（P2-5）

第二参考：`uiuxAndStyle/04-search-disclosure.md`。

## 1. 前置检查

- [ ] 通读 `HistoryTocPanel.vue + historyToc.ts`：当前是角色过滤 + 200 条分页 + 气泡跳转，无 readingWindow 概念——本阶段引入。
- [ ] 通读 `TurnGroupItem.vue + ToolCallCard.vue`：确认展开态是本地 `expanded` 还是全局 store（需迁到 per-session Map）。
- [ ] 确认搜索入口：全局搜索走 `workspaceStore` 还是独立 controller；有无防抖/取消。

## 2. 阶段目标

- 搜索：空 query 看 recents，query 只看命中；防抖+取消+request 计数防串；message 命中精确定位。
- 历史窗：进入即 `transcriptRunning=false`，杀运行指示，禁旧消息操作。
- Disclosure：显式选择优先，搜索导航自动开，嵌套/焦点/完成态都不误关。

## 3. 实现办法

### 3.1 搜索 `useSessionSearch.ts + SearchDialog` 语义

- 入口：`open` 即 `refreshSessions`（吞错）+ `active=0`；query 关窗后保留。
- 防抖：命令 80ms / 会话 150ms + query/close 变更取消；request 计数 guard 过期选择。
- 列表：`[New Task, session 行（+message 子行）, LoadMore, pages, settings, commands]`；`active` 钳 `optionCount-1`；hover 设 active；Enter 跑 active；Esc 关；输入滚入视。
- Recents：空 query 前 30 按 `updatedAt` 倒序；无标题草稿排除；归档 query 前隐藏；分组 Today/Yesterday/7d/30d/Earlier。
- Query：只收 controller 命中；过滤 scheduled-run；message 命中走 `navigateTranscript({sessionId,messageId,query})`；settings 经 dev 门；commands 按 query 实时搜。
- 状态行：`loading / failed+Retry / Load more(nextOffset!=null) / empty（全空且非 loading/error）`；行徽：metadataMatch/message 数/归档/运行点；命中 `<mark class=search-hit>`。

### 3.2 ReadingWindow 降级（改 `TimelineView/MessageTimeline`）

- 来源：`transcriptViews[session] = {hasMoreBefore/After, focus, historical, loading}`；`readingWindow = historical`。
- `transcriptRunning = isRunning && !readingWindow`：杀 run-activity/planning/working；tail 强制 `isActive=false`；live delegation 冻结。
- 窗内隐藏：`TurnOutcomeCard` 整个不渲染；`pendingPermission/askPending/planningState` 传 undefined（禁旧消息审批/问询/计划）。
- 例外：复制会话走 API 读全量，不扰动窗口。Markdown 侧无 details 折叠，只有 `useCopy`（1500ms flipped）+ 代码/图源码复制钮。

### 3.3 Disclosure `disclosure.ts`（新建，替换本地 expanded）

- 存：per-session `Map<key,{open,revealRequest}>`，只存显式选择，pane 驱逐保留，不落盘。
- 默认：`requestedOpen = choice?.open ?? (revealRequest 不匹配 ? true : automaticOpen)`；搜索导航首绘强制开。
- 嵌套：`open = requestedOpen && scope.visible`；父合子保留选择，重开恢复；子开向上传 claim 保祖先开。
- 焦点 guard：完成态 `auto-open true→false` 时若焦点在内/有选区则钉显式开；手动合且焦点在体则聚标题 + 通知 anchor hold 位。
- 整轮：`automaticOpen=isActive`；完工默认合、运行默认开、用户选择赢；失败但仍 active 保持开；`visibleSteps==0` 不渲染。
- 工具行：`automaticOpen=false` 永不自开，失败/拒绝只在 head 露出；体 `open && hasDetails && parentVisible` 才建块。
- 思考行：prop 控 autoOpen 默认合；`Show thinking` 关时 thinking-active 才紧凑指示，否则不渲染（文本保留）。

## 4. 验收标准

- [ ] 搜索：防抖+取消+过期 guard；空 query recents 分组；message 命中跳到行并自动展开。
- [ ] 窗内：运行灯灭、tail 不活、审批/问询/计划不可点、Outcome 卡消失。
- [ ] Disclosure：搜索目标自开；父合子记；完成不吞焦点；失败行 head 可见。
- [ ] 旧本地 `expanded` 布尔 scattered 状态删除，统一走 Map（破坏性）。
- [ ] `HistoryTocPanel` 分页 `PAGE=200` 保留或按 `SEARCH_PAGE_SIZE=60` 对齐（二选一，不兼容旧值）。

## 5. 风险

- per-session Map 常驻内存，pane 驱逐不释；上限按 06 LRU=20，允许。
