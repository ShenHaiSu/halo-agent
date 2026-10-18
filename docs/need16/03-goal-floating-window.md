# 03 Goal 浮动窗前端设计（可伸缩 + 进度展示）

> 触点（新）：`stores/goalStore.ts`、`components/goal/GoalFloatPanel.vue`；改：`views/WorkbenchView.vue`（挂载）、`bridge/hostBridge.ts`（类型+mock）、`styles/main.css`（2–3 个 token）。

## 1. 交互总览

```text
WorkbenchView（flex-col: Timeline + Composer + AskUserModal）
 └─ GoalFloatPanel（fixed 右下，z-40，absolute 于 section 内）
     ├─ expanded: 标题 + 描述 + 进度环/条 + 里程碑 checklist + 编辑/重置
     ├─ collapsed: 标题 + 百分比徽标（点击展开）
     └─ mini: 圆形目标按钮（仅 % 数字；无 Goal 时为“设目标”虚线圈）
```

- 默认态：有 Goal→`collapsed`；无 Goal→`mini` 引导；用户上次态 `localStorage halo-goal-panel-state` 记忆。
- 位置：`position:absolute; right:16px; bottom:88px`（Composer 上方），宽 320px（expanded），`max-height:46vh` 内部滚动；拖拽手柄可改 `right/bottom`（存 localStorage），**不做自由缩放宽高**（v1 只拖位，防布局发散）。
- 层级：低于 `AskUserModal`（modal 最高）、高于时间轴；流式生成中不自动弹（仅徽标 `%` 闪更新）。

## 2. `goalStore.ts`（新，Pinia）

```ts
export interface GoalMilestone { id: string; title: string; done: boolean; }
export interface SessionGoalDto { title: string; description: string; milestones: GoalMilestone[]; percent: number; note: string; updatedAtUtc: string; }
state: { goal: SessionGoalDto | null; loading: boolean; corrupted: boolean; panel: 'expanded'|'collapsed'|'mini'; pos: {right:number;bottom:number} }
actions:
  load(ws, sess)            // bridge.getGoal；corrupted→横幅标记
  save(ws, sess, draft)     // bridge.saveGoal（人工编辑）；UpdatedBy=user
  clear(ws, sess)           // 二次确认
  applyEvent(payload)       // goal:updated 订阅：同 sess 即替换 + updatedFlash=true 800ms
  setPanel()/movePos()      // 本地记忆
```

- 会话切换：`watch(workspaceStore.activeSessionId)` 即 `load`；子历史界面打开时浮动窗显示**父 Goal 只读**（标题后缀 `（父目标·只读）`，编辑按钮隐藏）。
- 与 `agentStore` 解耦：不进 turn-event 分发，独立 `haloEventBus.on('goal:updated')`（hostBridge 需把该 kind 透传，见 02 §6）。

## 3. `GoalFloatPanel.vue` 结构

```vue
<template>
  <div class="goal-float" :class="[panel, theme]" :style="posStyle" data-testid="goal-float-panel">
    <!-- mini -->
    <button v-if="panel==='mini'" data-testid="goal-mini-btn" @click="expand">◎ {{ pct }}% / 设目标</button>
    <!-- collapsed -->
    <div v-else-if="panel==='collapsed'" @click="expand">
      <span class="goal-title">{{ title||'尚未设定目标' }}</span>
      <n-progress type="circle" :percentage="pct" :show-indicator="false" />
      <button @click.stop="minimize">–</button>
    </div>
    <!-- expanded -->
    <div v-else>
      <header>🎯 目标 <drag-handle/> <button collapse/><button mini/></header>
      <div class="goal-body">
        <div v-if="!goal">空态 + [设定目标]</div>
        <div v-else>
          <h4>{{ goal.title }}</h4><p>{{ goal.description }}</p>
          <n-progress type="line" :percentage="pct" /> <span>{{ done }}/{{ total }} · {{ note }}</span>
          <ul>里程碑 checklist（只读勾选态；人工编辑态可勾）</ul>
        </div>
      </div>
      <footer>[编辑][重置]（只读时隐藏）</footer>
      <编辑抽屉：title/description/percent-slider/note/milestones（一行一标题）/>
    </div>
  </div>
</template>
```

- 组件：`n-progress(circle+line)`、`n-input`、`n-slider(percent)`、`n-dynamic-input(milestones)`、`n-empty` 空态——全 Naive 现成，不引入新依赖。
- 进度环颜色沿用 TopBar 水位仪三段（蓝/ amber/红，见 `WorkbenchTopBar.vue:318-330`），`percentDisplay` 同口径。
- 人工编辑保存：`save` 成功 `message.success('目标已保存，下一轮生效')`；失败回滚草稿。

## 4. 布局与样式规范

- token 只用：`bg-app/bg-surface/bg-elevated/border-subtle/text-primary/text-secondary/text-accent` + 进度三色；禁止硬编码 `#fff/#000`（暗色必崩，见 01-E11）。
- 阴影 `shadow-lg` + `border` + 圆角 `rounded-xl`，与 `PromptComposer .composer-box` 同语言。
- 响应式：窗口宽 < 900px 时 expanded 自动降为 collapsed（`matchMedia` 监听）；`94vw` 上限防小窗横溢。
- 动画：`transition-all 180ms` 高度/透明度；`prefers-reduced-motion` 时关闭。
- 拖拽：仅 header 手柄 `pointerdown→mousemove` 改 `--goal-right/--goal-bottom`；范围钳制在 section 内；Esc 取消拖拽。

## 5. `hostBridge.ts` 对接

```ts
// 类型
export interface SessionGoalDto { title: string; description: string; milestones: Array<{id:string;title:string;done:boolean}>; percent: number; note: string; updatedAtUtc: string; }
// HostBridgeApi 新增
getGoal: (ws: string, sess: string) => Promise<BridgeResponse<SessionGoalDto>>;
saveGoal: (ws: string, sess: string, goalJson: string) => Promise<BridgeResponse<SessionGoalDto>>;
clearGoal: (ws: string, sess: string) => Promise<BridgeResponse<boolean>>;
// mock（无后端/CEF 直开时）：内存 Map + 默认空 Goal，保证浮动窗可独立联调
// 事件：haloEventBus 透传 'goal:updated'（rawBridge 事件名与后端 Publish 一致）
```

## 6. data-testid 清单

`goal-float-panel / goal-mini-btn / goal-expand-btn / goal-collapse-btn / goal-minimize-btn / goal-save-btn / goal-reset-btn / goal-progress-bar / goal-milestone-item / goal-corrupt-warn`。

## 7. 不做

自由缩放宽高、Goal 历史版本、富文本描述（纯文本+v1 markdown 行内渲染复用 `renderMarkdown` 即可）、多 Goal 切换。
