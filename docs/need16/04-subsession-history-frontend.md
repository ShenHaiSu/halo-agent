# 04 子会话历史前端设计（面包屑三段 × 只读历史界面）

> 触点（新）：`stores/subSessionStore.ts`、`components/sub/SubHistoryView.vue`；改：`components/workbench/WorkbenchTopBar.vue`（三段面包屑）、`components/chat/ToolCallCard.vue`（入口按钮）、`components/chat/MessageTimeline.vue`（复用抽离）、`components/settings/AgentBehaviorView.vue`（运行表入口）、`components/chat/PromptComposer.vue`（子只读遮罩）、`bridge/hostBridge.ts`（详情类型+mock）。

## 1. 状态机

```text
mode: 'main' | 'sub'
main: TopBar[ws/session] + MessageTimeline(mainMessages) + PromptComposer(可用) + GoalFloat(可写)
sub : TopBar[ws/session/sub ▾ + ←返回] + SubHistoryView(subMessages 只读) + Composer(隐藏/只读条) + GoalFloat(父只读)
切换: enterSub(subId) → 保留主快照（不清空 sessionMessages/budget/telemetry）→ 拉子列表+子消息
返回: exitSub() → 原位恢复（零重拉；滚动位置保留）
```

- 主流式生成中允许进子历史（事件按 sessionId 分桶，`agentStore` 已有 `turn-event` sid 路由，见 `agentStore.ts:552` 后分支）；子 running 显示进度条 + 3s 轮询。
- 切回主自动停轮询；轮询失败 3 次即停 + 重试按钮。

## 2. 面包屑三段（`WorkbenchTopBar.vue` 改）

```vue
<div class="breadcrumb">
  <span>{{ wsName }}</span> /
  <button @click="exitSubIfInSub" :class="{link: mode==='sub'}">{{ sessionTitle }}</button>
  <template v-if="mode==='sub'"> /
    <n-dropdown :options="subOptions" @select="enterSub">
      <span data-testid="topbar-sub-crumb">{{ activeSubLabel }}({{ shortId }}) ▾</span>
    </n-dropdown>
    <n-button size="tiny" data-testid="topbar-back-main" @click="exitSub">← 返回主会话</n-button>
  </template>
</div>
```

- `shortId = subId.slice(-6)`；label 超 12 字截断（沿用现有 `truncate max-w-[200px]`）。
- 下拉项：同父全部子（`label + status徽标 + shortId`），running 置顶；点击切换（停旧轮询起新轮询）。
- 右侧模型选择器/水位仪在 sub 模式下显示**主会话**预算（子预算走详情接口，不挤占 TopBar）。

## 3. `subSessionStore.ts`（新）

```ts
state: {
  mode: 'main'|'sub';
  parentId: string; activeSubId: string;
  subs: SessionMetadata[];          // getSubSessions
  detail: SubDetailDto | null;      // getSubSessionDetail（状态+用量+task）
  messages: UnifiedChatMessage[];   // getSessionMessages(ws, subId)
  loading: boolean; error: string;
  polling: boolean; pollTimer: number|null;
}
actions: enterSub(ws,parent,sub) / exitSub() / refreshList() / refreshMessages() / startPoll()/stopPoll() / cancelSub()
```

- 与 `workspaceStore` 关系：**不复用** `sessionMessages/activeSessionId`（防主快照被覆盖）；只读 `activeWorkspaceId/activeWorkspace`。
- `detail` 字段（05 提供）：`{ subId,label,status,taskPrompt,createdByCallId,iterations,toolExecutions,input/output/cached,billedPrompt/billedCompletion,startedAtUtc,elapsedMs }`。

## 4. `SubHistoryView.vue`（新，只读时间轴）

- 复用策略：把 `MessageTimeline.vue` 的 `timelineItems` 归并抽为 `composables/useTimelineGroups.ts(messages) → { items }`；主/子共用归并 + `renderMarkdown` + 图片缩略图逻辑，**样式壳各自保留**（子加只读徽标头）。
- 头部（sticky）：`子会话 label · status徽标 · task 截断（可展开）· 用量行（IN/OUT/Cache%) · [掐断]/[刷新]`。
- 正文：`MessageTimeline` 同款轮次容器；右键菜单**整体禁用**（`openMenu` 短路 + title 提示“子历史只读”）；`ToolCallCard` 内 `spawn_subagent` 按钮隐藏（防套娃跳转）。
- 空态：`n-empty "子尚未输出（running 中…）"`；失败态：`status + errorMessage + [重试][返回主会话]`。

## 5. 三处入口（统一 `enterSub`）

| 位置 | 形态 | testid |
|---|---|---|
| `ToolCallCard(spawn_subagent)` 展开态底部 | `查看子会话 →` 主按钮（解析 `argsJson.task/label` + 结果 `subSessionId`；结果未回（running）则 disabled+“执行中…”） | `toolcard-open-sub` |
| 诊断监控区运行表行内 | `历史` 幽灵按钮 | `sub-runtime-open-history` |
| barrier 等待条（`PromptComposer.vue:15-17`） | `查看子进度（n/m）` 链接 | `composer-sub-open` |

- subId 来源优先级：`SubAgentResult.subSessionId`（tool 结果 JSON）> `getSubSessions` 按 `callId=CreatedByCallId` 反查 > 置灰不可点。

## 6. 只读守卫清单

- 隐藏 `PromptComposer` 输入舱，代之以 `n-alert info "子会话历史只读；返回主会话继续对话"`（保留高度防跳动）。
- `MessageTimeline` 右键（截断/分支/重放/删除）在 sub 模式短路。
- `cancelSubAgent` 保留（running 才显）；`compact/rename/pin` 全藏。
- `GoalFloatPanel` 切只读（03 已定）。

## 7. data-testid 清单

`topbar-sub-crumb / topbar-back-main / topbar-sub-dropdown / sub-history-view / sub-history-header / sub-status-badge / sub-task-toggle / sub-refresh-btn / sub-cancel-btn / sub-empty / sub-error / toolcard-open-sub / composer-sub-open`。

## 8. 不做

子内发消息、子历史截断/分支/删除、面包屑第四段、侧边栏列出子会话（保持 need13“默认只主可见”）。
