# need19-02 Goal 面板：抽屉内目标视图（共享面板抽取 + 生命周期迁移）

> 对应总览：[00-README](./00-README.md) R2。前置阅读：[01](./01-architecture-design.md) §2.1/§4.2。
> 本篇只处理「目标」这一块：怎样把现有浮动卡 `GoalFloatPanel` 的能力搬进 Drawer，同时**不破坏**既有浮动卡与既有 `data-testid` 验收。

---

## 1. 现状复盘（代码级）

### 1.1 后端能力已经足够

| RPC | 位置 | 语义要点 |
|---|---|---|
| `GetGoal(ws, sess)` | `Bridge/HostBridgeFacade.Goal.cs:27-41` | 子会话 id **自动归一到父**（`ParentSessionId`）；损坏返回 `Success` + `message` 含「损坏」 |
| `SaveGoal(ws, sess, json)` | `:43-63` | 校验 JSON → `GoalService.SaveGoal(..., "user")` → 发布 `goal:updated` |
| `ClearGoal(ws, sess)` | `:65-78` | 写入空 `SessionGoal` → 发布 `goal:updated`（**注意：清空也是发布，不是删除文件**） |

模型：`Core/Models/SessionGoal.cs:12-27`（`Title/Description/Milestones/Percent/Note/UpdatedAtUtc/UpdatedBy`，含 `HasContent/DoneCount` 派生属性——前端不依赖，前端自算）。

> **结论**：Goal 面板**不需要任何后端改动**，Drawer 只是多一个渲染位置。

### 1.2 前端现状与三个坑

| # | 现象 | 锚点 | 影响 |
|---|---|---|---|
| K1 | 全部 UI 与逻辑（展示 + 编辑 + 拖拽 + 事件）写在 `GoalFloatPanel.vue` 一个文件里（133 行） | `components/goal/GoalFloatPanel.vue:1-133` | 直接塞进 Drawer 会带上 `position:absolute/width:320px/拖拽手柄` 等浮动语义 |
| K2 | `goalStore.watchSession()` 在浮卡的 `onMounted` 调用（内部 `haloEventBus.on('goal:updated')` **没有返回取消订阅**） | `GoalFloatPanel.vue:113-118`、`goalStore.ts:91-99` | 若 Drawer 与浮卡**同时**调用一次，会注册 2 个事件监听，`updatedFlash` 双触发（无害但脏）；若浮卡被移除而 Drawer 未挂载，则**完全丢失** `goal:updated` 响应 |
| K3 | `watchSession()` 里 `watch([activeWorkspaceId, activeSessionId], …, {immediate:true})` 每次调用都新建 watcher | `goalStore.ts:91-99` | 同上，重复调用 → 重复 `load()`（并发两请求，后到者覆盖，通常无害但会闪烁） |
| K4 | `GoalFloatPanel` 的 `onSave` 只做「标题/描述/百分比/备注/里程碑标题」，**不保留** `milestone.done` 的原值 | `GoalFloatPanel.vue:88`：`draft.value.milestones[i]?.done` | 编辑里程碑文本后勾选状态可保留（因为按索引取），但**新增行**会丢勾选；Drawer 版需要修正为按 `id` 匹配 |
| K5 | 无「只读子会话」语义之外的父目标只读标识 | `GoalFloatPanel.vue:70,12` | 子会话模式下展示父目标并标「只读」；Drawer 需保持一致 |

## 2. 设计：`GoalPanelContent.vue`（共享面板）

### 2.1 组件契约

```ts
// src/components/goal/GoalPanelContent.vue
defineProps<{
  /** float：悬浮卡（保留拖拽/收起/迷你）；drawer：抽屉内（占满宽度、无拖拽） */
  mode: 'float' | 'drawer';
  /** 只读（子会话查看父目标）；由父级传入，组件内部不读 subSessionStore */
  readonly?: boolean;
}>();
const emit = defineEmits<{
  (e: 'collapse'): void;      // 仅 float 模式：请求父级切到 collapsed
  (e: 'minimize'): void;      // 仅 float 模式：请求父级切到 mini
}>();
```

> **为什么把 `readonly` 做成 prop 而不是内部读 store**：让 `GoalPanelContent` 零依赖（除 `goalStore`），可被任意宿主复用，也便于后续在 Settings → 智能体行为里复用。

### 2.2 内部结构（三块）

```text
GoalPanelContent
├─ [损坏告警条]  v-if goalStore.corrupted    ← 复用现有文案 + 「重置」按钮
├─ [信息区]
│   ├─ 标题（h4，超长 2 行截断，title 悬浮全文）
│   ├─ 描述（p，多行，drawer 模式最多 6 行 + 「展开」）
│   ├─ 进度：n-progress(line) + 「done/total · note」
│   └─ 里程碑列表：v-for，前置 checkbox（只读模式 disabled）
├─ [空态]       v-if !hasContent → 「尚未设定目标」+ 主按钮「设定目标」
└─ [编辑区]     v-if editing（n-input / n-input textarea / n-slider / n-dynamic-input）
     └─ [保存][取消][重置]
```

### 2.3 两种模式的差异（用 class 而非两套模板）

| 差异点 | `mode="float"` | `mode="drawer"` |
|---|---|---|
| 根 class | `goal-float`（absolute/width 320/max-height 46vh/overflow auto） | `goal-drawer w-full`（无定位、无固定宽） |
| 头部 | 「🎯 目标 + 拖拽手柄 + 收起 + 最小化」 | 「🎯 目标 + 刷新按钮」（拖拽/收起/最小化隐藏） |
| 描述区 | 全量展示（浮卡自身 `max-height` 滚动） | 超过 6 行折叠，给「展开/收起」 |
| 里程碑 | 纯文本列表 | checkbox（可勾选，仅非只读时）→ **要求后端也只存布尔**，符合 `GoalMilestone.Done` |
| 底部按钮 | 「编辑 / 重置」 | 「编辑 / 重置 / 刷新」统一放在内容区底部 sticky |

实现建议：**单一模板 + `:class="mode"` + `v-if="mode==='float'"` 包住拖拽手柄/收起/最小化**，避免双份维护。

### 2.4 里程碑勾选的落盘路径（K4 修复）

勾选后**不立刻** `SaveGoal`（避免每点一次就一次 RPC + 一次事件风暴），而是：

1. 先本地 `toggleMilestone(id)` 更新 `draft.milestones` 的 `done`；
2. 标记 `dirty = true`，底部出现「未保存」小黄点；
3. 用户点「保存」→ `goalStore.save(ws, sess, payload)`；
4. 或 2 秒防抖后自动保存（**推荐**，符合"目标边车文件"的轻量语义，但必须与「编辑中」互斥：`editing===true` 时不自动保存）。

```ts
const savePayload = () => ({
  title: draft.title.trim(),
  description: draft.description,
  percent: Math.max(0, Math.min(100, draft.percent)),
  note: draft.note,
  updatedAtUtc: new Date().toISOString(),
  milestones: draft.milestones
    .filter((m) => m.title.trim())
    .map((m) => ({ id: m.id || `ms_${Date.now()}_${nanoid6()}`, title: m.title.trim(), done: !!m.done })),
});
```

> `id` 的生成口径沿用原代码 `ms_${Date.now()}_${i}`（`GoalFloatPanel.vue:88`），改为 `ms_${base36(now)}_${rand6}` 以保证同毫秒多行不撞。

### 2.5 保存/重置的反馈

* 保存成功：`message.success('目标已保存，下一轮生效')`（文案保持，来自后端 `message` 字段，别在前端另写）。
* 保存失败：`message.error(res.message)`。
* 重置：沿用 `GoalFloatPanel` 的做法**不弹确认**？→ **改进**：Drawer 内重置有破坏性且空间足够，改为 `dialog.warning({ title:'重置目标', content:'将清空标题/描述/里程碑/进度，不可撤销。', positiveText:'重置' })`。浮卡保持原行为（不弹），减少既有交互变更。

### 2.6 数据为空 vs 加载中 vs 损坏 三态

| 状态 | 判定 | UI |
|---|---|---|
| 加载中 | `goalStore.loading === true` | 骨架条（3 行灰块）+ 顶部 `Loading` 图标 |
| 空 | `!title && !description && milestones.length===0` | 空态卡 + 「设定目标」 |
| 损坏 | `goalStore.corrupted === true` | 顶部琥珀色告警条 + 「重置」+ 其余区域正常渲染（后端已视为空） |
| 有内容 | 其余 | 信息区 |

> 现有 `goalStore.load` 里有一段「空目标时把 panel 从 collapsed 降到 mini」的逻辑（`goalStore.ts:45-52`），**Drawer 模式不参与**该状态机——`panel` 只服务浮卡。因此 `GoalPanelContent` 内**不要**调用 `setPanel`。若在 float 模式下仍需要该行为，保留在 `GoalFloatPanel` 外部逻辑里。

## 3. 生命周期迁移（K2/K3 修复）

### 3.1 唯一挂载点

```text
RightRailDrawer.vue (setup 顶部，无条件执行)
  onMounted(() => goalStore.watchSession());
```

`GoalFloatPanel.vue` 里的 `goalStore.watchSession()` **删除**（改为注释说明迁移点）：

```ts
onMounted(() => {
  // need19：watchSession 迁移到 RightRailDrawer（唯一挂载点），此处不再调用
  if (window.innerWidth < 900 && goalStore.panel === 'expanded') goalStore.setPanel('collapsed');
  window.addEventListener('keydown', onKey);
});
```

### 3.2 `goalStore.watchSession()` 幂等化（必改）

现状每次调用都会新建 `watch` 且重复注册事件；改为模块内守卫：

```ts
let bound = false;
let unwatchSession: (() => void) | null = null;
function watchSession() {
  if (bound) return;
  bound = true;
  const ws = useWorkspaceStore();
  unwatchSession = watch(
    () => [ws.activeWorkspaceId, ws.activeSessionId] as const,
    ([nws, nsess]) => { if (nws && nsess) void load(nws, nsess); },
    { immediate: true }
  );
  haloEventBus.on('goal:updated', (p: unknown) => {
    try { applyEvent(p as { sessionId: string; goal: SessionGoalDto }); } catch { /* ignore */ }
  });
}
```

**风险控制**：`bound` 是 store 闭包内的模块级变量（每个 Pinia store 实例一份）。若将来 store 被 `$reset` 或 HMR 重载，需在 dev 环境下暴露 `__resetGoalWatch()` 便于 HMR（可选，不阻塞合入）。

### 3.3 `load()` 的重复请求抑制（可选增强）

Drawer 打开时切 Tab / 切会话可能触发多次 `load`，加一个简单序列号：

```ts
let loadSeq = 0;
async function load(ws: string, sess: string) {
  const seq = ++loadSeq;
  if (!ws || !sess) { goal.value = null; return; }
  loading.value = true;
  try {
    const res = await getHostBridge().getGoal(ws, sess);
    if (seq !== loadSeq) return;            // 丢弃过期响应
    ...
  } finally { if (seq === loadSeq) loading.value = false; }
}
```

> 这是**纯增量**改动，不改变 `goalStore` 的对外签名与返回值；`GoalFloatPanel` / `AgentBehaviorView` 调用方零改动。

## 4. 与浮卡的并存策略

| 场景 | 期望 | 实现 |
|---|---|---|
| Drawer 关闭 | 主界面照旧显示浮动卡（可拖/收起/迷你） | `GoalFloatPanel` 不变 |
| Drawer 打开且 Tab=goal | 浮卡**自动收起为 mini**，避免与抽屉内容重复 | `watch(uiStore.rightRailOpen, (open)=>{ if (open && uiStore.rightRailTab==='goal' && goalStore.panel==='expanded') goalStore.setPanel('mini'); })` 放在 `RightRailDrawer` |
| Drawer 关闭后 | **不自动展开**（尊重用户上次选择） | 无操作；用户可点 mini 按钮展开 |
| 抽屉里编辑后关闭 | 浮卡显示最新值 | 同源 `goalStore.goal`，天然同步 |
| 浮卡里编辑后打开抽屉 | 抽屉显示最新值 | 同上；另在抽屉 Tab 切到 goal 时调用 `goalStore.load()` 兜底（防止他端写入） |

> ⚠️ 不做「隐藏浮卡」的开关：用户没说要去掉它，且它是既有验收项（`data-testid="goal-float-panel"`）。如果后续用户嫌重复，再按 06 §5 的选项 B 处理。

## 5. 抽屉内 Goal 面板的额外能力（低成本增强，可选）

| 能力 | 价值 | 成本 | 是否本期 |
|---|---|---|---|
| 「复制为 Markdown」 | 把目标贴给队友 / 贴进计划文档 | 复用 `copyTextToClipboard`（`utils/historyAnchor.ts:35-55`） | ✅ 做（10 行） |
| 「Agent 写入时间」展示 | 区分「user 手改」与「main 自动更新」（`UpdatedBy`） | 后端已有字段，但前端 DTO `SessionGoalDto` 无 `updatedBy`（`hostBridge.ts:400`） | ❌ 不做（需改 DTO 契约，本期后端零改动） |
| 里程碑拖拽排序 | 计划调整 | 中等（需 pointer 事件 + 保存） | ❌ 不做 |
| 进度条与里程碑自动联动 | 勾选里程碑自动算 `percent = done/total*100` | 小 | ✅ 做（仅在用户未手动拖过 slider 时联动，给一个「自动」小按钮回填） |

## 6. 验收点（本篇专属）

| # | 检查 | 通过标准 |
|---|---|---|
| G1 | 抽屉 Tab=goal 展示当前会话目标 | 与浮卡内容逐字一致（标题/描述/百分比/里程碑数/备注） |
| G2 | 抽屉内编辑保存 | `message.success('目标已保存，下一轮生效')`；浮卡立即同步；`goal.json` 落盘（可在数据目录核对） |
| G3 | 抽屉内重置 | 弹确认 → 确认后清空，里程碑为空，`percent=0`，浮卡变 mini |
| G4 | 里程碑勾选 | 勾选 → 2s 内自动落盘（或点保存落盘）；重开抽屉状态保留；新增行 id 不撞 |
| G5 | 子会话模式 | 抽屉 Tab=goal 显示父目标 + 「只读」标识；无编辑/重置按钮 |
| G6 | 损坏文件 | 手工把 `goal.json` 改成非法 JSON → 打开抽屉出现琥珀告警条 + 「重置」可用 |
| G7 | 事件驱动刷新 | Agent 一轮结束自动改写目标（后端 `UpdatedBy=main` 场景）→ 抽屉内值变化 + 短暂高亮（`updatedFlash`） |
| G8 | 切会话 | A→B→A 切换，抽屉 Goal 内容随之变化，无残留 A 的数据闪烁超过 300ms |
| G9 | 无重复监听 | 打开/关闭抽屉 5 次后，`goal:updated` 只触发 1 次 `updatedFlash`（可在 devtools 打 console 计数验证） |
