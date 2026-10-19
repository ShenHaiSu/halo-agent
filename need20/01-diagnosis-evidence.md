# need20-01 缺陷诊断：逐行代码证据

> 对应总览：[00-README](./00-README.md) §2。
> 本篇只回答"为什么会坏"，每条结论后跟可复查的代码锚点。行号以 2026-10-19 实现落盘版本为准。

---

## 1. F1 浮动按钮未移除：并存策略被当成硬约束

### 1.1 挂载点仍在

```vue
<!-- develop/src/Web.Client/src/views/WorkbenchView.vue:1-11 -->
<div ref="hostRef" ... data-testid="workbench-view">
  ...
  <GoalFloatPanel />        <!-- :7 浮卡仍挂载 -->
  <AskUserModal />
  <RightRailTrigger />
  <RightRailDrawer :to="drawerTo" />
</div>
```

- `WorkbenchView.vue:18` 仍 `import GoalFloatPanel from '@/components/goal/GoalFloatPanel.vue'`。
- 结论：Drawer 与浮卡同时存在，用户看到的"外部浮动按钮"就是它。

### 1.2 浮卡三态 UI 完整保留

```vue
<!-- develop/src/Web.Client/src/components/goal/GoalFloatPanel.vue:1-19 -->
<div class="goal-float" :class="[panel, { flash: updatedFlash }]" ... data-testid="goal-float-panel">
  <button v-if="panel === 'mini'" data-testid="goal-mini-btn" ...>◎ {{ pct }}% / 设目标</button>
  <div v-else-if="panel === 'collapsed'" ...> ... </div>
  <div v-else class="goal-expanded">
    <header class="goal-head"> ... 拖拽手柄/收起/最小化 ... </header>
    <GoalPanelContent mode="float" :readonly="readonly" />
  </div>
</div>
```

- `mini / collapsed / expanded` 三态、`goal-drag` 拖拽、`position: absolute; z-index: 40`（`:60`）全部保留。
- 设计来源：`docs/need19/02-goal-panel.md §4` 并存策略表：
  "Drawer 关闭时主界面照旧显示浮动卡" + "不做隐藏浮卡的开关"。
- 施工时该表被当成硬约束执行，故浮卡一字未删。用户新口径（抛弃浮动设计）直接推翻该表，本修复将其作废。

### 1.3 "打开抽屉收 mini" 是并存策略的残留

```ts
// develop/src/Web.Client/src/components/rightRail/RightRailDrawer.vue:95-104
watch(
  () => [ui.rightRailOpen, ui.rightRailTab] as const,
  ([open, tab]) => {
    if (open && tab === 'goal' && goalStore.panel === 'expanded') goalStore.setPanel('mini'); // ← 并存残留
    ...
  }
);
```

- 浮卡删除后该分支无意义，需同步删除（见 02 §2.3）。

---

## 2. F2 抽屉空内容：自指 Teleport + 零高度父级

### 2.1 包裹层是零尺寸 absolute 占位

```vue
<!-- RightRailDrawer.vue:1-3, 107-108 -->
<template>
  <div class="right-rail-host">   <!-- :2 -->
    <n-drawer ...>
```

```css
.right-rail-host { position: absolute; inset: 0; pointer-events: none; }  /* :108 */
```

- 该 div 自身无内容高度，`inset: 0` 相对的是 `WorkbenchView` 的 `relative` 容器（`WorkbenchView.vue:2`），视觉上透明不可见。
- 它存在的唯一目的是给 `:to` 提供"容器内挂载"的宿主感，但实际引入了自指（见 2.2）。

### 2.2 `:to` 指回自身所在容器（自指 Teleport）

```vue
<!-- RightRailDrawer.vue:10, 74-80 -->
<n-drawer ... :to="drawerTo" ... >   <!-- :10 -->
```

```ts
const props = defineProps<{ to?: string | HTMLElement }>();   // :74
const drawerTo = computed(() => props.to ?? 'body');          // :80
```

```vue
<!-- WorkbenchView.vue:10, 25-26 -->
<RightRailDrawer :to="drawerTo" />                             <!-- :10 -->
const hostRef = ref<HTMLElement | null>(null);                // :25
const drawerTo = computed(() => hostRef.value ?? 'body');     // :26
```

- 调用链：`WorkbenchView.hostRef`（右工作台根 div）→ 作为 `to` 传给 `RightRailDrawer` → `RightRailDrawer` 自身就渲染在该 `hostRef` 内部 → Naive 执行 Teleport 时把抽屉 DOM 塞回自己所在的容器。
- Naive 源码证据（`node_modules/naive-ui/es/drawer/src/Drawer.mjs:273`）：
  `to: this.to` 直接透传给 Teleport。`to` 为 HTMLElement 时即容器内挂载。
- 后果：抽屉定位上下文错乱；`n-drawer` 的 `position: fixed/absolute` 计算基准不再是 viewport/body，而是被塞入的中间容器，叠加 `pointer-events: none` 的父级，内容区高度计算失效。

### 2.3 内容区高度链断裂（"只有横线" 的直接成因）

```vue
<!-- RightRailDrawer.vue:22-54 -->
<n-drawer-content :title="null" closable :native-scrollbar="false"
  body-content-style="padding:0;height:100%;" class="right-rail-drawer-content">
  <div class="right-rail-root" data-testid="right-rail-drawer">  <!-- :29 -->
    <nav class="right-rail-tabs"> ... </nav>                     <!-- :30 -->
    <section class="right-rail-content"> ... </section>          <!-- :48 -->
  </div>
</n-drawer-content>
```

```css
/* RightRailDrawer.vue:109-114 */
.right-rail-root { display: flex; height: 100%; min-height: 0; }
.right-rail-tabs { width: 48px; flex: 0 0 48px; ... }
.right-rail-content { flex: 1 1 auto; ... padding: 10px 10px 8px; overflow: hidden; }
```

- `right-rail-root{height:100%}` 的百分比基准是 `n-drawer-content` 的 body 区；Naive 的 `.n-drawer-body` 默认 `display:flex; flex-direction:column` 但**无显式高度**（高度由抽屉容器 + Teleport 目标共同决定）。
- 自指 Teleport 后抽屉容器高度为 0 → `height:100%` = 0 → 48px rail 与内容区被压扁。
- 用户看到的"一条横线" = `n-drawer-content` 的 header 区（承载 X 按钮的那一行，header 有自身高度不依赖 body）；"一个关闭按钮" = header 内的 X。body 区高度为 0，所以 rail + 内容区不可见。
- 这也解释了为什么 `data-testid="right-rail-drawer"` 在 DOM 里存在（`displayDirective` 默认 `if`，打开时渲染），但视觉上是空的。

### 2.4 对照组反证：SettingsCenterDrawer 为何正常

```vue
<!-- src/components/settings/SettingsCenterDrawer.vue:2-8 -->
<n-drawer v-model:show="visible" placement="right" :width="1020"
  style="max-width: 94vw;" :mask-closable="true">
```

- 它**不传 `to`**（默认 `body`），无自指，内容区高度链完整，故显示正常。
- 反证：本期独有的"容器内 `to` + 自指宿主"写法是 F2 的引入点；`need19-01 §1.1` 要求"绑定宿主 ref"的本意是"只盖右工作台"，但实现时多包了一层 `absolute` div 并把抽屉塞回自己内部，走偏了。

### 2.5 触发按钮不受影响（排除干扰项）

```vue
<!-- src/components/rightRail/RightRailTrigger.vue -->
.right-rail-trigger { position: absolute; right: 14px; top: 50%; ... z-index: 60; }
```

- 触发按钮是 `WorkbenchView` 内的普通 absolute 元素，不经过 Teleport，故能正常显示与点击。F2 与它无关。

---

## 3. 影响面确认（未被污染的部分）

| 模块 | 是否受 F1/F2 牵连 | 依据 |
|---|---|---|
| `GoalPanelContent` | 否 | 纯展示/编辑组件，无 Teleport、无浮动定位，可直接复用 |
| `goalStore.watchSession` 幂等 | 否 | 挂载点在抽屉内，浮卡删除后仍有效 |
| `TimelineNav` 注册表/跳转锁 | 否 | 在 `MessageTimeline/SubHistoryView` 内，与 Drawer 挂载方式无关 |
| `historyToc` 纯函数 | 否 | 零 DOM 依赖，自检 12/12 通过 |
| `HistoryTocPanel/HistoryBubble` | 显示层面被 F2 连带（高度 0 看不见），逻辑无坏 | 修复挂载后自动恢复 |

---

## 4. 诊断小结

- F1 = 需求口径变更（need19 并存策略作废），删代码即可；
- F2 = 实现 bug（自指 Teleport + 高度链断裂），改挂载方式即可；
- 两者正交，可同批施工、分别验收（见 03）。
