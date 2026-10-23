# 10 Minimap + 平滑流文本 + Settle 幕布（P2-4）

第二参考：`uiuxAndStyle/02-minimap.md + 03-smooth-settle.md`。

## 1. 前置检查

- [ ] 确认当前无 minimap、无平滑 reveal、无 settle 幕布（`TimelineView` 直接全量渲染）。
- [ ] 确认 `naive-ui + tailwind` 下可加绝对定位 rail（左侧 8px 宽 26px 通道）。
- [ ] 确认 `useAgentStream` 可提供 `isActive/status=streaming` 供逐气泡平滑门控。

## 2. 阶段目标

- 左缘 Codex 式 dash 栈：user 宽 / assistant 窄，点击跳转，hover 放大+预览。
- 流文本 60ch/s 平滑 reveal，积压 500ms 追平，大文本直通。
- 首屏 bounded 提交用幕布遮挡 + 3 帧稳定才揭，期间禁 minimap/回到底。

## 3. 实现办法

### 3.1 新建 `ConversationMinimap.vue + conversation-minimap.ts`

- 数据：每 user 一 marker + 每 assistant 回复合并一 marker（首个 contentful 片定位；空/纯 thinking/tool 无 marker；预览 280ch 空行拼接）。
- 样式：user 13px / assistant 8px；历史 marker 13px 虚线；hover 次色；active 全主色 + `aria-current`；`--magnify` 只横向放大不重排（transition 90ms）。
- 交互：只 click/hover/focus，无拖拽。点击 → `releaseFollow + scrollTo(offset-24px)`（尊重 reduced-motion）；顶部虚线 → `onRevealEarlier`；加载中 disabled + `aria-busy`。
- 放大：macOS-Dock 余弦，`RADIUS=46px / BOOST=1.3×`，命令式写 `--magnify`；popover `SNAP=24px / 高 132 / top=clamp(center-36,0,railH-132)`，宽 248px，最多 5 行，`pointer-events:none`。
- active：`scrollTop + 30% 视口` 二分最近 marker；偏移缓存，resize/marker 变/内容 resize 才重算，不在滚动帧算。
- 门控：`hasEarlier || (markers≥2 && overflow>1px)` 才挂；`paneVisible && !veilCovering` 才量；只用已挂载行建 markers；rail 高跟 composer dock（ResizeObserver）；0~1 marker 无 earlier → 隐藏。

### 3.2 新建 `useSmoothText.ts + smooth-cursor.css`

- 门控：`settings.smoothStreaming !== false && !reducedMotion && isActive && status==='streaming'`；源 `>32KB` 直通。
- 循环：rAF ≤60Hz，`dt=min(elapsed,100)/1000`；`base 60ch/s / lag>30ch 时 speed=backlog/0.5`；小数进位；代理对 guard；disable/complete/unmount 全量 flush。
- 光标：`.smooth-cursor` 仅 streaming 显示。`AssistantTurnParts` 式拆分：`SmoothBubble(useSmoothText+rAF) + TurnPartBlock memo`。

### 3.3 新建 `transcriptSettle.ts + 幕布`

- 相位 `covering→leaving→off`；幕布 `absolute inset:0` 不透明骨架，`pointer-events:auto` 但低于 composer（可打字）；wrap 打 `data-transcript-settling`。
- 判定：从扩窗提交起每 rAF 采样 `{scrollHeight,clientHeight}`，连续 `STABLE_FRAMES=3` 或 `MAX_MS=600` 即稳；淡出 `160ms`（与 CSS 同值）；短会话直接 `off`。
- 采样期间仍 pinned 则每帧 re-pin 到最新；`veilCovering` 阻塞 minimap 挂载和 jump pill；另有 `navigation-loading` 独立状态。

## 4. 验收标准

- [ ] minimap：markers≥2 或有 earlier 才出现；点击跳转 ±24px 且不回抢；hover 放大+预览；active 随滚动二分切换。
- [ ] 平滑：60ch/s 体感、积压追平、大文本直通、reduced-motion 关闭、unmount 不丢字。
- [ ] settle：首屏无白闪跳动；幕布期可打字；600ms 上限必揭；短会话无幕布。
- [ ] 无障碍：rail `nav+aria-label`，dash `aria-label`，loading `aria-busy`，错误 `role=status`。
- [ ] 旧“流式纯 escape 整块闪”行为删除（破坏性）。

## 5. 风险

- 左 rail 占 26px+8px，小窗挤压时间轴；允许，小窗可按溢出阈值自动隐藏。
