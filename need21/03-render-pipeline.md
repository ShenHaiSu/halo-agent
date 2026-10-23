# need21-03 渲染管线（markdown / 懒挂载 / 滚动 / 工具卡）

> 本篇是性能主战场的具体实现。所有数值均为初值，P3 验收时按探针微调，只允许收紧、不允许放宽。

---

## 1. Markdown 两档（`MarkdownStream.vue`，119 行）

现状：`status=streaming` 时每 30ms 对全文 `md.render(tolerantMarkdown)+hljs`（L76-107），单轮 O(L²）。

### 1.1 新规则

| 状态 | 渲染 | 说明 |
|---|---|---|
| `streaming` | 纯文本 `<pre class="stream-plain">`，只显示尾部 2000 字（`text.slice(-2000)`），头部记 `…省略N字` | 零 `md.render`、零 hljs、零 `tolerantMarkdown`；`useRafThrottle` 去掉，改为按帧直接赋纯文本（单次赋值 <0.1ms） |
| `completed / interrupted / failed` | 现有 `refreshFinal` 全量 `md.render` 一次，结果缓存在 `finalHtml`（L93-102 保留） | 缓存 key 追加文本 hash：`(rowId + len + fnv(text))`，同值跳过 |

### 1.2 diff 点（照抄）

* 删除：`throttledText`、`rendered` 的 streaming 分支、`watch(status→text)` 的 streaming 触发。
* 保留：`tolerantMarkdown`、`truncateByParagraph(limit=8000)`、`breaks:true`、`highlight` 缓存、`md-truncated` 提示、`onLinkClick`。
* `TextBlockView`（55 行）不动：继续传 `text/status/rowId`，`streamStatus` 映射保留（`streaming→streaming`）。
* `limit` 缺省 8000 不变；流式纯文本档与终态 markdown 档切换时保留滚动位置（`useStickToBottom` 逻辑不动）。

### 1.3 为什么视觉一致

* 流式期用户本来就看不清 markdown（30ms 闪烁 + 未闭合围栏靠 `tolerantMarkdown` 猜），纯文本 + 等宽反而更稳；终态一次成型与现在逐字 identical（同一 `md` 实例、同一 `highlight`）。
* `data-testid="markdown-stream" / data-row-id / data-status` 不变。

## 2. turn 级懒挂载（新增 `useInViewport.ts`，复用 `ToolCallCard` 模式）

现状：`windowedTurnUnits` 只做“近 30 轮”窗口（`MessageTimeline L150-151`），窗口内全量挂载；`ToolCallCard` 已有 `IntersectionObserver(rootMargin 240px)→heavyVisible`（L273-296），但 turn 正文没有。

### 2.1 新规则

* 新建 `composables/useInViewport.ts`（约 40 行，抄 `ToolCallCard L274-296`，`rootMargin` 取 `600px` turn 级、body 级沿用 240px）。
* `TurnUnitView` 根按 turn 包装：屏外 turn 只渲染 `UserBubble` 首行 + 占位块（高度按 `blocks.length` 估算，每块 48px，最小 96px，保证滚动条不跳），`TextBlockView/ReasoningBlock/ToolGroupCard.Body` 进视口才挂载；`HeaderMemo/StatusBar` 常驻（已隔离，见 `ToolCallCard` L12-35）。
* `nav.register/unregister`（`useTimelineScroll L228-234`）不受懒挂载影响：占位块同样带 `data-row-id` 注册锚点，跳转时先挂载再 `scrollIntoView`（`TimelineNav` 已有能力，见 need19-03）。
* 图片（`useImageThumbs`）与 `n-image` 保持现有 miss 回填逻辑，仅挂载时机延后。

## 3. 工具卡（`ToolCallCard.vue`，322 行：只收紧，不重排）

保留三段隔离（HeaderMemo / StatusBar / BodyLazy，L12-53）与 `legacyToRow` 兼容分支（P1 删除旧链路后移除，见 05 P4）。

| 项 | 现状 | 新值 |
|---|---|---|
| 终端输出流式上限 | 无（`outputText` 全量进 `TerminalBody`） | 流式只传尾部 2000 字 + 前缀 `…（流式仅显示尾部，终态全量）`；终态走现有 6000 字截断（`registry L165`） |
| 参数预览 | `useToolArgs` Map 缓存（上限 300，key `rowId:len`） | 保留；`parseToolArgs` 不动；`balanceClose` 只在 `complete=false` 且长度变化 >256B 时跑（加长度门） |
| header 重算 | `headerSig` dev 计数（L310-321） | 保留；验收要求流式期单卡 header 翻转 ≤3 次（建卡/定稿/终态） |
| 审批单 | `pendingApproval` 直通 | 不动 |

## 4. 滚动（`useTimelineScroll.ts`，70 行）

现状：`allKeys.length` 变化即双 rAF + 200ms 保底（L28-33），长会话每帧两次强制同步布局。

新规则：

1. 贴底门控：`el.scrollHeight - scrollTop - clientHeight < 120` 才跟随，否则只更新 `NewMessagesPill` 计数（组件已存在）。
2. 删除 200ms 保底 timer，只留单 rAF；`wheel/touchstart/keydown` 解锁逻辑保留（L34-41）。
3. markdown 档切换（纯文本→终态）不触发滚动（高度变化由占位估算吸收，误差 >200px 才校正一次）。

## 5. pill（替代 active 容器正文区的唯一新增 UI）

* 新建 `components/chat/ActiveTurnPill.vue`（约 60 行，样式抄 `active-turn-container` 外框 + `L74-78` 头部行，复用 `BrainCircuit/Loader2` 图标）。
* 内容（2Hz 更新，`useLiveStatus` 从投影 streaming 行派生，不读 agentStore 累积串）：
  `思考中… · 已收 X 字 · N 个工具（M 运行） · 已用 Ts` + `终止`按钮（现有 `cancelAgentTurn` 逻辑搬入）。
* testid 兼容：根节点同时带 `data-testid="active-turn-container"` 与 `data-testid="active-turn-pill"`，旧 e2e 不断。
* 空态：无 streaming 行且 `!running` 时不渲染（替代旧 `showActiveContainer` 的恒 true 门）。

## 6. 推理块（`ReasoningBlock.vue`，109 行：逻辑不动，只换数据源）

* `text/status/isStreaming/emptyStreaming` 计算保留；`previewLine`（首非空行去 markdown 符号截 120）保留；`whitespace-pre-wrap + max-h-96` 保留。
* 数据源从 `messagesToRows` 的 `reasoningContent` 切到投影 `reasoning` 行（字段同名，`buildTurnUnits` 已产出 `bkind=reasoning`，`assembleBlocks` L208）。
* 自动收起（`autoCollapseKey` + `isStreaming` watch）保留——chat 协议推理量大，默认收起是性能的一部分（不渲染即零成本）。
