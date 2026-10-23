# 09 历史滚动：follow/自由 + ShowJump + 分页（P2-3）

第二参考：`uiuxAndStyle/01-history-scroll.md`。前置依赖：`06`（history/tail 分离）已合入。

## 1. 前置检查

- [ ] 通读 `useTimelineNav.ts`：当前只有 `register/scrollToAnchor/scrollToBottom`，无 `pinned/showJump` 状态机、无手势归因——本阶段重写。
- [ ] 通读 `TimelineView.vue + useTimeline.ts`：确认流更新是否经 `scrollToBottom` 硬滚（会抢用户阅读）。
- [ ] 确认 `HistoryTocPanel.vue` 跳转走 `scrollToAnchor`，与本阶段 `pinned` 互斥关系待定。

## 2. 阶段目标

- `pinned（follow）+ showJump` 纯 reducer；发送/重试/流提交自动跟尾，上翻即释放并出回到底。
- 顶部两段式：先扩窗（`+40`），窗满才 `loadOlder`；prepend 锚定视口不跳。
- 隐藏 pane 永不跟随/分页/采样；显隐保留滚动。

## 3. 实现办法

### 3.1 新建 `transcriptScroll.ts`（`features/session/utils/`）

- `reduceTranscriptScroll(distanceFromBottom, gesture)` → `{pinned, showJump: !pinned}`；常量 `REPIN_THRESHOLD_PX=48 / ROUNDING=1（手势路径 0）/ GESTURE_WINDOW_MS=200`。
- 手势归因：`wheel/touchstart/touchmove/pointerdown/keydown（ArrowUp/Down/PageUp/Down/Home/End/Space）` 且落在滚动面、非输入框、非嵌套消费 → 手势；1px 上滚即解 pinned。
- 挂载：`pinned = saved?.pinned ?? true`；`showJump = !pinned`；pinned 才 bottom。

### 3.2 改 `useTimelineNav.ts` 为状态机

- 新增 `pinnedRef/showJump`（响应式）+ `releaseFollow/jumpToLatest`；`jumpToLatest` = `pinned=true + scrollTo({smooth|auto}) + onReturnToLatest()` 清 reading 窗。
- 发送/重试 rising edge → 取消 holds、`pinned=true`、layout 阶段 `scrollToBottom`；流提交/ResizeObserver 增长且 pinned → observer 回调 pre-paint bottom。
- 上翻/`releaseFollow`（minimap 跳、disclosure 展开、搜索导航）→ `pinned=false, showJump=true`。
- `#324` disclosure hold：展开工具/思考标题先 hold 标题位置，hold 期间已排队的 follow 帧让路，真输入才释放。
- `!paneVisible` 直接 return：不跟随/不分页/不采样。

### 3.3 分页与窗口

- 常量 `INITIAL_MOUNT=15 / WINDOW_MIN=60 / STEP=40 / TRANSCRIPT_PAGE_SIZE=100`。
- 首帧挂尾部 15 + hydration spacer `min-height:100vh`，次帧扩全窗（仍 pinned 才 re-bottom）；`useDeferredValue` 延迟 history（readingWindow/首提交/reveal 除外）。
- 顶部：近顶（120px，镜像 IO `rootMargin:120px 0 0 0`）先 `growWindow(+40)`；窗覆盖全部已载才 `loadOlder`；prepend 用 `scrollTop += scrollHeight delta` 锚定。
- `hasMoreAfter` → `loadLater` 按钮；顶部 loading 行 `role=status aria-live=polite`。

### 3.4 ShowJump 按钮

- 显示条件 `(showJump || readingWindow) && !veilCovering`；32px 圆、底部 `composer-dock+12px`、入场动画；点击 = `onReturnToLatest + jumpToLatest`。

## 4. 验收标准

- [ ] 单测：reducer 上翻解 pinned、48px 内回 pin、1px 手势解绑。
- [ ] 行为：流式时上翻不被抢回底部；回到底按钮出现；发送即回尾。
- [ ] 顶部连续上滚：先扩窗后加载，不跳视口；隐藏 pane 切回滚动不变。
- [ ] 旧硬滚 `scrollTop = scrollHeight` 路径删除（破坏性）。
- [ ] 文案键：`chat.scrollToBottom/loadingEarlier/showEarlier/loadLater/loadingSession`（无 i18n 则硬编码中文同义，键名保留注释）。

## 5. 风险

- 手势阈值 1px 偏灵敏，触板用户易解 pinned；允许，后续按 08 回调调参。
