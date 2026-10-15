# 02 · 会话删除二次确认：菜单保持展开、末项变红“确定删除”方案

## 1. 现状（已读代码）与需求差异

- 现状（need02-04 已落地）：`case 'delete'` 只 `armDeleteConfirm`（L599-601），卡面 hover 区 morph 出卡外红色 pill `确定删除？`（L236-250/L329-343），dropdown 本体关闭。状态机 `pendingDeleteSessionId + 3s timer + Esc/切卡取消`（L435-475）。
- 新需求：**菜单保持展开**，第一次点删除后**菜单不关闭**，最后一个按钮（仍在菜单内）变红、文案变“确定删除”，鼠标不动、原地再点一次即确认。Fitts 距离为零，且符合“菜单内完成”的心智。
- 即：确认态从“卡面 pill”搬进“dropdown 菜单末项”，pill 方案下线。

## 2. 目标交互（精确到像素与事件）

1. hover 卡 → 点 `···` → dropdown 展开（重命名/置顶/复制hash/导出md/divider/删除会话）。
2. 第一击点“删除会话”：**菜单不关闭**（拦截 Naive dropdown 默认选中即关闭行为），该行原地变为红色 `确定删除`（图标 `Trash2` 保留，文字 `text-error` 加粗，可加 `bg-red-500/10` 行底），其余行保持可点但点他行即取消 armed。
3. 第二击点同一行（mousedown+mouseup 一次完整 click，无需移动鼠标）：执行 `workspaceStore.deleteSession`，菜单关闭，toast `会话已删除`。
4. 取消路径：`Esc` / 点击菜单外 / 切会话 / 切工作区 / 3s 超时 → armed 复位，菜单可保持展开但末项恢复普通态，或直接关闭菜单（推荐关闭，最干净）。
5. 防呆：行内重命名中（`editingSessionId` 非空）禁止 arm；删除请求中（`deletingSessionId` 非空）禁止连点。

## 3. 详细设计

### 3.1 状态机（复用现有，改触发点）

```ts
const pendingDeleteSessionId = ref<string|null>(null); // 已有，保留
const deletingSessionId = ref<string|null>(null);      // 已有，保留
// arm/cancel/isArmed/confirmDeleteInline 逻辑不变，confirm 成功后多调 dropdownShow=false
```

- 新增 `sessionDropdownShow: Record<string, boolean>`（或 `openMenuSessionId: string|null`）控制 `n-dropdown` 的 `show`（受控模式 `:show` + `@update:show`），是“保持展开”的关键：
```html
<n-dropdown :show="openMenuSessionId === sess.id" @update:show="(v) => onDropdownShowChange(v, sess.id)" ...>
```
- `handleSessionMenuSelect('delete', sess)` 改为：
```ts
if (!isArmed(sess.id)) { armDeleteConfirm(sess.id); /* 不关闭菜单 */ return; }
// 已 armed → confirmDeleteInline(sess.id); openMenuSessionId = null;
```
- Naive `n-dropdown @select` 默认会关闭菜单 → 必须用受控 `show` 顶住：第一击后显式 `openMenuSessionId = sess.id` 保持；只有第二击成功/取消才置 `null`。

### 3.2 菜单选项渲染（末项变红）

- `getSessionDropdownOptions(sess)` 末项改为动态：
```ts
const armed = isArmed(sess.id);
{ label: armed ? '确定删除' : '删除会话', key: 'delete',
  props: armed ? { style: 'color: var(--error-color, #ef4444); font-weight: 600;' } : undefined,
  icon: () => h(Trash2, { class: 'w-4 h-4 ' + (armed ? 'text-error' : 'text-error') }) }
```
- Naive dropdown option 支持 `props.style` 直写行内样式（Chrome109 安全），armed 时整行红色；如需整行底色，用 `props: { class: 'delete-armed-row' }` + `main.css` 定义 `.delete-armed-row { background: rgba(239,68,68,.12) !important; }` 及其 `html.light` 版。
- `label` 必须精确为 `确定删除`（需求原话，不带问号/空格），`data-testid` 建议菜单容器加 `data-testid="session-menu"`、末项无法直接加 testid（Naive 内部渲染）→ 自动化改从“连续两次 select delete”断言。
- 图标不换（仍 `Trash2`），避免用户误认功能变更。

### 3.3 模板改动（删除卡面 pill，瘦身两处 hover 区）

- 删除 L236-250 / L329-343 的 `v-if="isArmed"` pill 分支，hover 区恢复纯 dropdown（`n-dropdown` 受控 show）。
- 卡根 armed 红边 `:class="isArmed(...) ? 'border-red-500/60 bg-red-500/10' : ..."` **保留**（菜单展开时卡面仍有红色警示，与菜单末项红色呼应）。
- `handleSelectSession` 首行 `cancelDeleteConfirm()` 保留，另加 `openMenuSessionId = null`；全局 `Escape` 同步清两者；3s timer 回调同时清 `openMenuSessionId`（若菜单还开着则关闭）。

## 4. 施工步骤

1. `WorkspaceSessionSidebar.vue` script：新增 `openMenuSessionId` + `onDropdownShowChange(v, id)`（`v===false && isArmed(id)` 时的策略：推荐直接 `cancelDeleteConfirm()`，即点外关闭=取消，与 3s 超时语义一致）；`handleSessionMenuSelect` 的 `delete` 分支按 §3.1 改两态；`confirmDeleteInline` 成功后 `openMenuSessionId = null`。
2. `getSessionDropdownOptions` 末项按 §3.2 动态 label/style；其余四项不动。
3. 模板两处 hover 区：删 pill `n-button` 分支，`n-dropdown` 加 `:show` 受控与 `@update:show`；卡根红边保留。
4. `main.css` 加 `.delete-armed-row` 两套（dark/light）——若行内 style 已足够红，可跳过此步，但推荐保留类以便亮色下底色可读。
5. Mock（`hostBridge.ts`）与后端不动；`workspaceStore.deleteSession` 不动。

## 5. 验收清单

- [ ] 第一下点“删除会话”后菜单**仍展开**（截图证明），末项文字变为 `确定删除` 且为红色；鼠标不动第二下点同一行即删除 + toast，菜单关闭。
- [ ] `Esc`/点菜单外/切会话/3s 超时 → armed 复位，末项恢复“删除会话”普通态（或菜单关闭，二者皆可，关闭优先）。
- [ ] 重命名中点删除无反应；删除请求中连点只发一次请求（`deletingSessionId` 守卫）。
- [ ] 置顶卡与常规卡行为一致；`grep -n "confirm-delete-btn\|n-modal\|showDeleteConfirm" WorkspaceSessionSidebar.vue` 中 pill/btn 应为零命中（新方案无卡面 pill），红边 `isArmed` 保留。
- [ ] 暗/亮截图：常态菜单、armed 菜单（末项红色）各一；`vue-tsc -b && vite build` 通过。

## 6. 风险与回退

- 风险：Naive `n-dropdown` 受控 `show` 与 `trigger="click"` 冲突导致菜单打不开 →  LR：先以 `:show="undefined"` 非受控验证选项渲染，再切受控；或改用 `@clickoutside` 手动关。
- 风险：第一击后 Naive 内部仍强制关闭 → 兜底：在 `handleSessionMenuSelect` 第一击末尾 `nextTick(() => openMenuSessionId = sess.id)` 强制重开，用户视角仍是“没关闭”。
- 回退：恢复 need02 pill 分支（保留本篇 git diff 即可一键 revert），状态机与 timer 两篇共用，无需重写。
