# need19-07 边界与异常场景（Edge Cases）

> 所有「不是主干路径」的情况集中在此，施工时逐条对照实现，验收时逐条打勾。
> 每条格式：**触发条件 → 现象（若不处理）→ 处理策略 → 归属文档/代码点**。

---

## 1. 会话与数据类

### E-01 空会话
* **触发**：新建会话，`sessionMessages.length === 0`。
* **现象**：面板一片空白，用户以为坏了。
* **处理**：`HistoryTocPanel` 空态：`Bot` 图标 + 「当前会话还没有消息」+ 次要文案「在下方输入指令后，这里会出现可跳转的历史目录」；触发按钮仍可点。
* **归属**：05 §1 / M1。

### E-02 会话加载中
* **触发**：`workspaceStore.loadingMessages === true`（`workspaceStore.ts:29`，切会话时置位）。
* **现象**：面板闪一下旧会话内容。
* **处理**：
  1. 用 `loadingMessages` 显示骨架（3~5 个灰条气泡）；
  2. **额外**：监听 `activeSessionId` 变化时立即 `activeKey = null`（避免高亮残留在新会话里）；
  3. 不在 loading 期间允许点击（骨架不可点）。
* **归属**：05 §1。

### E-03 会话被删除 / 工作区被移除
* **触发**：左栏删除当前会话（`workspaceStore.deleteSession`，`workspaceStore.ts:318-340`）。
* **现象**：Drawer 仍显示旧消息。
* **处理**：`deleteSession` 会把 `sessionMessages` 清空（`:330-332`）→ 目录 computed 自动变空态；Goal 的 `load()` 因 `sess=''` 把 `goal=null`（`goalStore.ts:37`）→ 面板空态。**无需额外代码**，但要验证不报错。
* **归属**：02/05 空态。

### E-04 压缩上下文（`/compact`）
* **触发**：顶栏「压缩上下文」。
* **现象**：历史被替换为摘要 + 保留末 N 组；旧的锚点 key 全部失效。
* **处理**：事件 `CompactCompleted` → `refreshAfterTool(sid,'finish')` → `loadSessionMessages`（`agentStore.ts:499-507`）→ `sessionMessages` 换新数组 → 目录 computed 重建，`activeKey` 若不在新 keys 中则置 null。
* **归属**：03 §4.3 / M17。

### E-05 历史被截断/重放/分支/删除消息
* **触发**：右键菜单 → 重放/分支/删除（`MessageTimeline.vue:405-436`）。
* **现象**：同上，key 变化。
* **处理**：这些操作都在内部调用 `loadSessionMessages` 重拉（`workspaceStore.ts:518,540-541,576`）→ 自动重算。**注意** `branchFromMessage` 会 `selectSession(created.id)` 切到新会话，目录随之刷新。
* **归属**：05 §1。

### E-06 超长会话（2000+ 条）
* **触发**：长跑会话。
* **现象**：一次性渲染 2000+ 气泡卡顿；`buildTurnGroups` 也是 O(n)。
* **处理**：L1 分批（首屏 200，滚动 200/批）。
* **已知重复计算（实证）**：`buildTurnGroups` 会被算两遍——一次在 `MessageTimeline.allTurnGroups`（`MessageTimeline.vue:177`），一次在 `HistoryTocPanel` 自己的 computed。n=2000 时约 2×3ms（单次 O(n) 纯循环，无 DOM），可忽略。
  **优化项（P4 可选）**：把「`sessionMessages` + `hiddenToolCallIds` → `visibleTurnGroups` → `timelineEntries`」提升为 Pinia getter（新建 `timelineStore` 或挂到 `workspaceStore`），两处共享同一份缓存。**本期不做**，避免牵动 `MessageTimeline` 的核心计算路径（R-03 红线成本高）。
* **归属**：05 §4。

### E-07 消息内容极大（单条 200KB 的粘贴）
* **触发**：用户粘贴大段日志。
* **现象**：`toDisplayText` 正则跑在大字符串上，可能卡 10~30ms。
* **处理**：**先截断再清洗**：`toDisplayText` 内部开头 `s = s.slice(0, 4000)`（气泡只需要前 15 字，但 tooltip 要 60 字，4000 足够）；`fullText` 也只存前 4000。避免对 200KB 做 9 次全局正则。
* **归属**：05 §2.2。

## 2. 跳转与锚点类

### E-08 点击后找不到锚点
* **触发**：Timeline 尚未渲染 / 子会话未加载完 / 该条目被 hidden 过滤。
* **处理**：`scrollToAnchor` 返回 `false` → `message.warning('未找到该消息，可能正在加载或已被压缩')`；不抛异常。
* **归属**：03 §4.3。

### E-09 `chatWidthMode === 'centered'`
* **触发**：默认外观。
* **现象**：容器内容居中，`max-width:56rem`（`main.css:111-115`）。
* **处理**：用 rect 差值计算，天然正确。**不要**用 `offsetTop`（居中容器的 offsetParent 链不同）。
* **归属**：03 §4.1。

### E-10 Drawer 打开时窗口尺寸变化
* **触发**：拖主窗口边缘。
* **现象**：`to` 是右工作台容器，Drawer 宽度不变但位置随容器变化（右侧贴合）。
* **处理**：无额外代码（绝对定位相对容器）；但要验证 M18。
* **归属**：04 D10。

### E-11 Drawer 打开时拖 `n-split`
* **触发**：用户拖左栏分隔条。
* **现象**：遮罩覆盖右工作台，分隔条可能被挡住或拖到一半被遮。
* **处理**：接受（透明遮罩会吞掉鼠标事件）。若分隔条在遮罩之外（左栏侧）仍可拖 → Drawer 跟随右工作台变化，正常。
* **归属**：04 D9。

## 3. Goal 类

### E-12 `goal.json` 损坏
* **触发**：手工改坏 / 断电写坏。
* **现象**：`GetGoal` 返回 `Success(空)` + message 含「损坏」（`HostBridgeFacade.Goal.cs:33-34`）。
* **处理**：`goalStore.corrupted = true`（`goalStore.ts:44`）→ 面板琥珀告警条 + 「重置」按钮；重置走 `clearGoal`。**不允许**自动覆盖（用户可能想手工修复文件）。
* **归属**：02 §2.6 / G6。

### E-13 无工作区 / 无会话
* **触发**：首次启动未添加工作区。
* **现象**：`activeWorkspaceId=''`。
* **处理**：`goalStore.load` 直接 `goal=null`；面板显示「请先选择工作区/会话」；目录同理。
* **归属**：02 §2.6。

### E-14 子会话查看父目标
* **触发**：`subSessionStore.mode === 'sub'`。
* **现象**：后端 `ResolveGoalTargetAsync` 把子 id 归一到父（`HostBridgeFacade.Goal.cs:13-25`），返回父目标。
* **处理**：面板 `:readonly="true"`，标题旁标「父目标·只读」，隐藏编辑/重置；里程碑 checkbox 置灰。
* **归属**：02 §2.1 / G5。

### E-15 并发写（主进程 Agent 自动更新 + 用户手动保存）
* **触发**：Agent 在一轮结束时自动更新目标（`UpdatedBy=main`），用户同时在抽屉里编辑。
* **现象**：`goal:updated` 事件到达 → `applyEvent` 覆盖 `goal`，用户正在编辑的 `draft` 不受影响（draft 是组件局部），但保存时会以 draft 覆盖 Agent 的写入。
* **处理**：
  1. `editing === true` 时收到 `goal:updated` → 在编辑区顶部提示「目标已被 Agent 更新，你的编辑将以保存为准」+「载入最新」按钮；
  2. 不自动丢弃用户输入（尊重用户）。
* **归属**：02 §2.5（补充）。

## 4. Drawer / UI 类

### E-16 `to` 宿主元素未就绪
* **触发**：首帧 `hostRef === null`。
* **处理**：`computed(() => hostRef.value ?? 'body')`；`show=false` 时 Naive 不渲染内容，无实际影响。
* **归属**：01 §1.1。

### E-17 与设置中心同时打开
* **触发**：Drawer 打开时点顶栏「设置」。
* **现象**：两个 right drawer 叠在一起。
* **处理**：Naive 的 zindex 递增管理（`zindexable`），后打开者在上；关闭设置中心后右侧栏恢复。**不做互斥关闭**（用户可能想对照配置）。
* **归属**：04 D11。

### E-18 Win7 x86 / CefSharp 109
* **触发**：老机器。
* **风险点**：
  * `Array.from()`、`performance.now()`、`IntersectionObserver`、`scrollTo({behavior:'smooth'})` 在 Chrome 109 **全部支持**（IntersectionObserver 自 Chrome 51、smooth scroll 自 Chrome 61、`Array.from` 自 45）；
  * CSS 用到的只有 flex、transitions、`@keyframes`、`var()` —— 全部安全；
  * **禁止**：`:has()`（Chrome 105+ 才支持，但 109 支持！仍需避免——项目 `main.css:146` 明确要求）、`container query`、`color-mix()`、`scrollbar-gutter`。
* **处理**：代码评审时 grep 一遍 `:has(`、`color-mix`、`container-type`。
* **归属**：M20。

### E-19 高 DPI 缩放（125%/150%）
* **触发**：Windows 显示缩放。
* **现象**：CefSharp 使用 device scale factor；`getBoundingClientRect` 返回的是 CSS px，不受影响。
* **处理**：无需特殊代码；仅需验证 M18（不同分辨率）。

### E-20 极窄窗口（< 900px，浮卡会自动折叠）
* **触发**：`GoalFloatPanel.onMounted` 里有 `window.innerWidth < 900` 判断（`:115`）。
* **处理**：Drawer 宽度 `min(360, 86vw)`，rail 仍 48px；内容区在 360px 下可读。极窄（<480px）时 `HistoryBubble` 的 `max-width:78%` 下 15 字会 ellipsis，可接受。
* **归属**：04 D10。

## 5. 流式与实时事件类

### E-21 流式生成中
* **触发**：`agentStore.hasActiveTurn === true`。
* **现象**：
  * `sessionMessages` 不变（只有 `AssistantMessagePersisted` 后才 `loadSessionMessages`）；
  * `visibleTurnGroups` 里可能隐藏了正在流的 tool 组（`agentStore.hiddenToolCallIds`）。
* **处理**：
  * 目录最后追加一个"脉冲气泡"（`正在生成…`，不可点，`aria-disabled`）；
  * 生成完成 → `TurnCompleted` → `finishTurn` → `loadSessionMessages` → 真实条目出现，脉冲消失；
  * **去重**：脉冲气泡不参与 `index` 编号（否则编号会跳）。
* **归属**：05 J5。

### E-22 轮次被中断/截断（`turnNotice`）
* **触发**：达到 25 轮迭代（`TurnTruncated`）或用户停止。
* **现象**：时间轴出现 `n-alert` 提示。
* **处理**：目录不受影响（`turnNotice` 不进入 `sessionMessages`）；但 assistant 气泡可加 `⚠` 徽标（从 `agentStore` 拿不到历史轮次的通知，**本期不做**）。
* **归属**：05 §2.4（不做）。

### E-23 子代理（need13）并发
* **触发**：主会话触发子代理，`subSessions` 有多个。
* **现象**：主会话时间轴可能插入子代理相关条目（若后端落盘）。
* **处理**：目录只反映 `sessionMessages` 实际内容，子代理自己的历史在子会话视图里看。无额外处理。
* **归属**：无。

## 6. 交互冲突类

### E-24 右键菜单与 Drawer 打开
* **触发**：Drawer 打开时右键 Timeline（遮罩会拦，实际点不到）。
* **处理**：无需处理（遮罩吞事件）。Drawer 关闭时菜单正常（R-05）。
* **归属**：01 §3.3。

### E-25 Esc 多义性
* **触发**：Drawer 打开 + 右键菜单打开 + 目标编辑中。
* **现象**：Esc 可能同时被多个监听消费。
* **处理**：
  * Drawer 的 Esc（Naive 内部）会关闭 Drawer；
  * `MessageTimeline` 的 `bindMenuGlobalClose` 绑在 `window` 上（`:437-441`），会关菜单；
  * `GoalFloatPanel` 的 `onKey` 在拖拽时清 `dragOff`（`:112`）；
  * 三者互不冲突（各管各的）。**注意**：Drawer 内输入框按 Esc（非输入法组合）会关 Drawer——若用户觉得烦，把 Q1 改 `:close-on-esc="false"`。
* **归属**：04 §6 Q1。

### E-26 IME 组合输入
* **触发**：中文输入法。
* **处理**：Naive `handleEsc` 有 `isComposingRef` 保护；`HistoryTocPanel` 若加搜索框需同样处理（本期无搜索框，过滤是下拉）。
* **归属**：04 §5.3 / D8。

## 7. 持久化类

### E-27 `localStorage` 不可用（隐私模式/配额满）
* **触发**：`setItem` 抛异常。
* **处理**：`uiStore` 的所有读写包 `try/catch`（与 `goalStore` 现有风格一致，`goalStore.ts:16-21,28-34`）；失败仅降级为"本次会话内有效"。
* **归属**：P0。

### E-28 旧版本遗留 key
* **触发**：`halo-goal-panel-state` 等既有 key。
* **处理**：不动。新增 `halo.ui.rightRail.v1`（JSON：`{tab, width, hintShown}`），读取时字段校验 + `try/catch`。
* **归属**：P0。

### E-29 宽度越界（用户缩放到 200px 后窗口变小）
* **处理**：写入前 `clamp(width, 320, 720)`，读取时同样 clamp；渲染时 `:width="clampedWidth"`。
* **归属**：04 D5。

## 8. 异常路径兜底（"永远不白屏"原则）

| 失败点 | 兜底 |
|---|---|
| `getGoal` 抛异常 | `goalStore.load` 已有 `finally` 但无 `catch`（`goalStore.ts:36-55`）→ **补 catch**：`goal.value = null`，面板显示「目标加载失败」+ 重试按钮 |
| `loadSessionMessages` 失败 | store 已把 `sessionMessages=[]`（`:353-358`）→ 目录空态；不额外报错（避免噪音） |
| 组件渲染异常 | 在 `RightRailDrawer` 内容区外包一层 **自实现的 `PanelBoundary`**（`onErrorCaptured` + `ref(hasError)` + 「重试」按钮，约 25 行）。**不要**依赖 `n-error-boundary`——实测 `naive-ui@2.40.x` 的 `es/` 目录**没有** `error-boundary` 组件（`es/` 下仅有 alert/card/... 等 100+ 目录，无 error 相关项），写了会构建失败。 |
| `scrollToAnchor` 抛异常 | 内部 try/catch → 返回 false |

> `PanelBoundary` 放在 `src/components/rightRail/PanelBoundary.vue`，随 P0 一起实现（10~25 行，无依赖）。

## 9. 边界验收汇总表

| # | 边界 | 验收方法 | 期望 |
|---|---|---|---|
| X1 | E-01 空会话 | 新建会话打开面板 | 空态，不报错 |
| X2 | E-02 加载中 | 快速切 5 个会话 | 无旧数据残留，骨架正常 |
| X3 | E-04 压缩 | 点压缩按钮 | 目录重建，无 JS 错误 |
| X4 | E-06 长会话 | 构造 2000 条测试数据 | 打开 < 1.5s，滚动流畅 |
| X5 | E-07 巨型消息 | 粘贴 200KB 文本 | 打开面板无卡顿 |
| X6 | E-08 失锚 | 删掉某条后立刻点旧气泡（极端） | 提示警告，不崩 |
| X7 | E-12 损坏目标 | 手工改坏 `goal.json` | 告警条 + 重置可用 |
| X8 | E-14 子会话 | 进入子会话看 Goal tab | 只读 + 父目标标识 |
| X9 | E-15 并发写 | 编辑中由 Agent 更新目标 | 提示"已被更新"，输入不丢 |
| X10 | E-17 双 Drawer | 右侧栏 + 设置中心 | 都能开关，层级正确 |
| X11 | E-20 窄窗 | 900×600 | 布局可用 |
| X12 | E-21 流式 | 生成时看目录 | 脉冲气泡，结束后正确去重 |
| X13 | E-27/28 存储异常 | devtools 里禁用 localStorage 后重开 | 功能可用，仅不记忆 |
| X14 | E-29 宽度 | 拖到极值后重启 | 宽度被 clamp |
| X15 | 8 兜底 | 临时在 `getGoal` mock 抛错 | 面板显示失败态 + 重试 |
