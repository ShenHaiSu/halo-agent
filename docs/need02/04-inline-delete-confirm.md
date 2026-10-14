# 04 · 会话删除原地二次确认（替代 Dialog）方案

## 1. 现状盘点（已读代码）

`WorkspaceSessionSidebar.vue` 删除链路共 5 段：

| 位置 | 现状 |
|------|------|
| L231-244 / L306-318 | 每卡 hover 才出现的 `MoreVertical → n-dropdown`（rename/pin/copy-hash/export-md/delete） |
| L467-499 `getSessionDropdownOptions` | `delete` 选项为普通 dropdown item（无危险色文字，仅图标 `text-error`） |
| L530-533 | `case 'delete': sessionToDelete=sess; showDeleteConfirm=true` |
| L399-400 / L537-545 | `showDeleteConfirm + sessionToDelete + confirmDeleteSession()`（调 `workspaceStore.deleteSession`，成功 toast） |
| L344-354 | `n-modal preset=dialog type=warning` 全屏弹窗二次确认 |

痛点：Dialog 打断流、需二次瞄准（Fitts 定律：弹窗按钮与原卡距离远），且与「批量整理会话」心智不符。

## 2. 目标交互：原地两下确认

- 第一击（dropdown「删除会话」或 hover 垃圾桶）：该卡操作区**原地 morph 为红色确认态** `确定删除？`（pill 按钮，占据原 dropdown 按钮位，不位移布局）。
- 第二击（点击该 pill 本体）：即执行删除；3 秒内无第二击自动复位；`Esc`/切换会话/滚动/点击他处取消。
- 整个过程**不弹 Modal、不跳 toast 打断**（成功仅轻量 `message.success('会话已删除')` 保留），减少瞄准时间：两次点击落点同一像素区。

## 3. 详细设计

### 3.1 状态机（单文件内）

```ts
const pendingDeleteSessionId = ref<string | null>(null);
let pendingDeleteTimer: number | null = null;
function armDeleteConfirm(sessId: string) {
  pendingDeleteSessionId.value = sessId;
  resetTimer();
  pendingDeleteTimer = window.setTimeout(() => { pendingDeleteSessionId.value = null; }, 3000);
}
function cancelDeleteConfirm() {
  pendingDeleteSessionId.value = null;
  if (pendingDeleteTimer) { clearTimeout(pendingDeleteTimer); pendingDeleteTimer = null; }
}
async function confirmDeleteInline(sessId: string) {
  cancelDeleteConfirm();
  const ok = await workspaceStore.deleteSession(sessId);
  if (ok) message.success('会话已删除');
}
function isArmed(sessId: string) { return pendingDeleteSessionId.value === sessId; }
// 全局 Esc：onMounted 注册 keydown，Escape → cancelDeleteConfirm()
// 切换会话 handleSelectSession 首行加 cancelDeleteConfirm()
```

- 单一 `pendingDeleteSessionId` 天然保证同时最多一个卡处于确认态（新 armed 自动覆盖旧 timer，与 05 篇排他展开同心智）。
- 删除进行中：`deletingSessionId` 可复用同一 ref + `n-button loading`，避免连点；`workspaceStore.deleteSession` 本就按 `activeWorkspaceId+sessionId` 删除并自动切到首个剩余会话（L250-272），无需额外跳转逻辑。

### 3.2 模板改动（两处卡，共用同一片段，推荐抽 `SessionCardActions` 或至少复制对称）

推荐落点：**保留 dropdown，其内「删除会话」第一击只 arm；卡面 hover 区在 armed 时替换 dropdown 按钮为确认 pill**——兼顾「不改变现有菜单结构」与「原地确认」：

```html
<!-- 替换 L232 / L307 的 hover 操作区 -->
<div class="opacity-0 group-hover:opacity-100 transition-opacity ml-1.5 shrink-0 flex items-center" @click.stop>
  <!-- 确认态：原地 pill，第二击即删 -->
  <n-button v-if="isArmed(sess.id)" size="tiny" type="error" secondary
    data-testid="confirm-delete-btn"
    @click="confirmDeleteInline(sess.id)" @mouseleave="cancelDeleteConfirm">
    <template #icon><AlertTriangle class="w-3.5 h-3.5" /></template>
    确定删除？
  </n-button>
  <!-- 常态：原 dropdown -->
  <n-dropdown v-else trigger="click" :options="getSessionDropdownOptions(sess)"
    @select="(k) => handleSessionMenuSelect(String(k), sess)">
    <n-button quaternary circle size="tiny" data-testid="session-more-btn"><MoreVertical class="w-3.5 h-3.5" /></n-button>
  </n-dropdown>
</div>
<!-- armed 时卡片本体加红边提示 -->
:class="isArmed(sess.id) ? 'border-red-500/60 bg-red-500/10' : (sess.id===activeSessionId ? ... : ...)"
```

- 备选（更激进）：dropdown 内 `delete` 改为 `render` 自定义红色行，第一击后 dropdown 不关闭、行内变「再次点击确认删除」——需操作 Naive dropdown 内部状态，复杂度高，**不推荐**。
- `AlertTriangle` 已在 import（L375），无需新增图标；`n-modal` 相关 `showDeleteConfirm/sessionToDelete/confirmDeleteSession`（L399-400/L537-545/L344-354）整段删除。

### 3.3 视觉与可达性

- 确认 pill：`type=error + secondary`，文字 12px `确定删除？` 不截断；卡边 `border-red-500/60 + bg-red-500/10`，亮色下对应 `html.light` 覆盖为 `#fef2f2` 底（参考 need01 语义类做法，可直接复用 `.setting-option-active` 的反转思路或内联 Tailwind `light:`——推荐内联 `bg-red-500/10` 在亮色亦可读，实测后再定）。
- `data-testid`：`session-more-btn`（常态）、`confirm-delete-btn`（确认态）供自动化「点两次删」用例。
- 键盘：`Esc` 取消；pill 可聚焦回车触发（`n-button` 原生支持）。
- 防呆：正处行内重命名（`editingSessionId===sess.id`）时屏蔽删除（dropdown 本就可点但应禁 pill，可在 `armDeleteConfirm` 首行 `if (editingSessionId.value) return`）。

## 4. 施工步骤

1. `WorkspaceSessionSidebar.vue` script：新增 §3.1 状态机（`pendingDeleteSessionId + timer + arm/cancel/confirm/isArmed`）；删 `showDeleteConfirm/sessionToDelete/confirmDeleteSession`；`handleSessionMenuSelect` 的 `case 'delete'` 改为 `armDeleteConfirm(sess.id)`；`handleSelectSession` 首行 + 全局 `handleGlobalKeydown` 加 `Escape → cancel`；`onUnmounted` 清 timer（与现有 keydown 监听同处 L554-560）。
2. 模板两处（L184-245 置顶卡 / L258-319 常规卡）：操作区替换为 §3.2（确认 pill vs dropdown 二选一渲染）；卡根 `:class` 追加 armed 红边分支。
3. 删 L344-354 `n-modal` 整块；检查 `n-modal` import 无残留（Naive 按需全局注册则无 import，无需动）。
4. `getSessionDropdownOptions` 的 `delete` 项 label 改为 `删除会话（需二次确认）` 或保持 `删除会话`（推荐保持短文案，二次性由 pill 表达，避免菜单行过长）。

## 5. 验收清单

- [ ] 无 `n-modal` 删除弹窗：`grep -n "showDeleteConfirm\|n-modal" WorkspaceSessionSidebar.vue` 零命中（工作区移除的 `n-popconfirm` L101 不在删除范围，勿动）。
- [ ] 第一下（菜单点删除）→ 原卡位出现红色 `确定删除？`，布局无跳动；第二下点 pill → 会话消失 + toast；3s 不点自动复位；`Esc`/点他卡/切会话立即复位。
- [ ] 误删防护：armed 卡切走/刷新列表后不残留；删除进行中连点不发第二次请求。
- [ ] 置顶卡与常规卡行为一致；`data-testid=confirm-delete-btn` 可被自动化连续点击两次删除。
- [ ] 暗/亮截图（常态/确认态各一）；`vue-tsc -b && vite build` 通过。

## 6. 风险与回退

- hover 丢失导致 pill 点不到（鼠标移出 `group-hover` 区）：pill 本体在 hover 区内，`@click.stop` 已阻止冒泡选会话；触屏无 hover——dropdown 本体点击仍可 arm，pill 为常驻按钮可点，无死路。
- 回退：恢复 `case 'delete'` + `n-modal` 三件套，删 pill 分支即可。
