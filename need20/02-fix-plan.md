# need20-02 修复施工清单（diff 级，可照抄执行）

> 对应总览：[00-README](./00-README.md) §3；诊断见 [01](./01-diagnosis-evidence.md)。
> 顺序：S1（删浮卡）→ S2（修挂载）→ S3（清残留）→ S4（验证）。每步可独立编译。

---

## S1 删除浮动卡（F1）

### S1.1 `WorkbenchView.vue` 去挂载

```diff
 <template>
   <div ref="hostRef" class="flex-1 min-h-0 flex flex-col bg-app overflow-hidden relative" data-testid="workbench-view">
     <MessageTimeline v-if="sub.mode === 'main'" />
     <SubHistoryView v-else />
     <PromptComposer v-if="sub.mode === 'main'" />
     <n-alert v-else type="info" class="mx-4 mb-2 text-xs">子会话历史只读；返回主会话继续对话</n-alert>
-    <GoalFloatPanel />
     <AskUserModal />
     <RightRailTrigger />
     <RightRailDrawer :to="drawerTo" />
   </div>
 </template>
 <script setup lang="ts">
 import MessageTimeline from '@/components/chat/MessageTimeline.vue';
 import PromptComposer from '@/components/chat/PromptComposer.vue';
 import AskUserModal from '@/components/chat/AskUserModal.vue';
-import GoalFloatPanel from '@/components/goal/GoalFloatPanel.vue';
 import SubHistoryView from '@/components/sub/SubHistoryView.vue';
```

### S1.2 删除文件

```cmd
del develop\src\Web.Client\src\components\goal\GoalFloatPanel.vue
```

- 全仓确认无残留引用：`findstr /n "GoalFloatPanel" src\ -r` 应只剩本文档与 need19 过程稿。
- `goalStore.panel/pos/setPanel/movePos` 保留（`GoalPanelContent` 不用，但删除 store 字段会扩大回归面；如需清理另立 need）。

---

## S2 修复抽屉挂载与高度链（F2，核心）

### S2.1 去掉自指包裹层，`to` 回退 body

```diff
 <template>
-  <div class="right-rail-host">
-    <n-drawer
-      v-model:show="ui.rightRailOpen"
-      ...
-      :to="drawerTo"
+  <n-drawer
+    v-model:show="ui.rightRailOpen"
+    ...
+    to="body"
```

```diff
-const props = defineProps<{ to?: string | HTMLElement }>();
 const ui = useUiStore();
 ...
-const drawerTo = computed(() => props.to ?? 'body');
 const isSubReadonly = computed(() => sub.mode === 'sub');
```

- `WorkbenchView.vue:10` 同步改为 `<RightRailDrawer />`（不再传 `:to`），`drawerTo/hostRef` 如无他用可保留（`hostRef` 仍用于 `relative` 定位上下文，无害；也可删除）。
- 决策说明：回退 `body` 后抽屉为整窗右侧 overlay，会盖住左侧工作区侧栏。这是为"内容可见"付出的临时代价；若用户要求"只盖右工作台"，后续 P2 再做"无 Teleport 的内联侧栏"（见 §5 备选），本期先保证可用。

### S2.2 高度链兜底（三处，缺一不可）

```diff
 <!-- n-drawer-content：body 给显式高度 -->
 <n-drawer-content
   :title="null"
   closable
   :native-scrollbar="false"
-  body-content-style="padding:0;height:100%;"
+  body-content-style="padding:0;height:100%;min-height:0;display:flex;flex-direction:column;"
   class="right-rail-drawer-content"
 >
```

```diff
 /* scoped：根给视口高度兜底，不再依赖父级百分比 */
-.right-rail-host { position: absolute; inset: 0; pointer-events: none; }  /* 整行删除 */
-.right-rail-root { display: flex; height: 100%; min-height: 0; }
+.right-rail-root { display: flex; height: 100vh; max-height: 100vh; min-height: 0; }
 .right-rail-tabs {
   width: 48px; flex: 0 0 48px; display: flex; flex-direction: column; gap: 6px;
   padding: 10px 6px; background: var(--bg-sidebar); border-right: 1px solid var(--border-subtle);
 }
-.right-rail-content { flex: 1 1 auto; min-width: 0; display: flex; flex-direction: column; min-height: 0; padding: 10px 10px 8px; overflow: hidden; }
+.right-rail-content { flex: 1 1 auto; min-width: 0; display: flex; flex-direction: column; min-height: 0; padding: 10px 10px 8px; overflow: hidden auto; }
```

- 为什么 `100vh`：`to="body"` 后抽屉容器高度由 Naive 的 fixed 定位保证，`100vh` 是与之对齐的最简兜底；`chat-timeline-centered` 等主界面样式不受影响（抽屉 overlay 不占轨道）。
- `overflow: hidden auto`：纵向可滚、横向不溢出（与 `settings-drawer-body` 同策略）。

### S2.3 图标 rail 正方形约束（防"横线"复发）

```diff
+/* scoped 追加：rail 按钮强制正方形，避免被 flex 压扁 */
+.right-rail-tabs .rail-tab { flex: 0 0 36px; width: 36px; height: 36px; margin: 0 auto; }
```

- 全局 `.rail-tab`（`main.css`）已有 `height:36px`，此处补 `flex-basis/width` 是为了在 `flex-direction:column` 下不被压缩。
- 图标 `w-[18px] h-[18px]` 保持（Tailwind 任意值在 Chrome109 安全）。

---

## S3 清理并存残留

### S3.1 删除"打开 goal 收 mini 浮卡"分支

```diff
 watch(
   () => [ui.rightRailOpen, ui.rightRailTab] as const,
   ([open, tab]) => {
-    if (open && tab === 'goal' && goalStore.panel === 'expanded') goalStore.setPanel('mini');
     if (open && tab === 'goal') {
       const wsId = ws.activeWorkspaceId || '';
       const sessId = ws.activeSessionId || '';
       if (wsId && sessId) void goalStore.load(wsId, sessId);
     }
   }
 );
```

### S3.2 `goalStore` 不动

- `panel/pos/setPanel/movePos` 保留；`watchSession` 幂等守卫保留；`load` 序号抑制保留。
- 理由：删除 store 字段需同步改 `AgentBehaviorView` 等零散写入点（`need19-00 §2` K 表），扩大回归面。

---

## S4 验证命令（每步后执行）

```cmd
:: S1 后：应报 GoalFloatPanel 引用缺失（如有残留），修净后应过
cd develop\src\Web.Client
call pnpm exec vue-tsc -b

:: S2+S3 后：全量门禁
call pnpm exec vue-tsc -b && call pnpm build

:: 纯函数回归（不受本次改动影响，应恒绿）
node --experimental-strip-types scripts/toc.selfcheck.mjs
```

---

## 4. 风险与回滚

| 风险 | 概率 | 对策 |
|---|---|---|
| `to="body"` 后抽屉盖住左侧栏，用户觉得"遮挡太多" | 中 | 接受（本期先可用）；后续 P2 按 §5 备选改内联侧栏 |
| 删 `GoalFloatPanel` 后某处残留 import 导致构建失败 | 低 | `vue-tsc` 会精确定位，按报错删 import 即可 |
| `100vh` 在 Win7 高 DPI 缩放下多出 1px 滚动条 | 低 | `max-height:100vh + overflow:auto` 已兜底；验收 M18 覆盖 |
| `n-drawer-content` header 高度变化导致 rail 顶部不对齐 | 低 | rail 是 body 内 flex 子项，与 header 无关 |

回滚：`git revert` 本次单个 commit 即可（S1+S2 同一 commit 内，revert 后浮卡与旧抽屉同时恢复）。

---

## 5. 备选（本期不做，记录供后续）

- **P2 内联侧栏**：不用 `n-drawer`，在 `WorkbenchView` 右侧加 `flex` 轨道（宽 360，可折叠），彻底实现"只盖右工作台 + 主界面可操作"。代价：`chat-timeline-centered` 需重排验证（need19-00 §4.4 红线），单独立项。
- **`goalStore.panel` 清理**：删除 `panel/pos` 及 `halo-goal-panel-state` 读写，需同步改 `AgentBehaviorView`，另立小 need。
