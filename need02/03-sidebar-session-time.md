# 03 · 左侧边栏会话卡：去 hash、展示创建与最后消息时间方案

## 1. 现状盘点（已读代码）

`WorkspaceSessionSidebar.vue` 两处会话卡（置顶 L184-245 / 常规 L258-319）第二行完全同式：

```html
<!-- L223-227（置顶），L297-301（常规）同 -->
<div class="flex items-center gap-2 text-[10px] text-secondary font-mono mt-0.5">
  <span>{{ formatTokens(sess.totalInputTokens + sess.totalOutputTokens) }} Tokens</span>
  <span>·</span>
  <span :title="`完整会话 Hash: ${sess.sessionHash}`">Hash: {{ sess.sessionHash.substring(0, 6) }}</span>
</div>
```

- 数据源 `SessionMetadata`（`hostBridge.ts` L272-286）：`createdAtUtc/updatedAtUtc`（ISO）、`messageCount/totalInput/OutputTokens/isPinned` 均已就绪，后端 `SessionRepository` 持续维护 `updatedAtUtc`（发消息/改名/置顶即更新），**无需 C# 改动**。
- 现状问题：6 位 hash 对用户无意义（复制走 dropdown `copy-hash` 即可，`WorkbenchTopBar` L107-123 与状态栏已展示完整 hash），却长期占用第二行；用户真正需要的是「哪个是旧会话、哪个刚活跃」。
- 搜索逻辑 `workspaceStore.ts` L44-53 按 `title/sessionHash/id` 过滤——去展示不影响搜索，可保留 hash 检索能力。

## 2. 目标：简洁的一行时间

- 卡上**不再出现任何 `Hash:` 字样**；第二行改为：`创建时间 · 最后活跃`（+ 可选 Tokens 精简保留）。
- 单行 `truncate` 不换行；相对时间为主、绝对时间进 `title` tooltip；置顶/常规两处同式，仅抽公共函数/组件避免双份漂移。

## 3. 展示文案设计（推荐方案 A）

```html
<div class="flex items-center gap-1.5 text-[10px] text-secondary font-mono mt-0.5 truncate" :title="sessionTimeTitle(sess)">
  <span>{{ formatSessionTime(sess.createdAtUtc) }}</span>
  <span class="opacity-50">·</span>
  <span>活跃 {{ formatRelativeTime(sess.updatedAtUtc) }}</span>
</div>
```

- 示例渲染：`10-12 创建 · 活跃 2分钟前`；当天显示 `今天 14:32`，7 天内 `3天前`，跨年 `2025-11-02`。
- Tokens 去留二选一（推荐**去掉 Tokens**，理由：顶栏已有上下文水位仪，边栏每卡重复 Tokens 信息密度低；若产品坚持保留，备选 `10-12 · 2分钟前 · 3.2K` 三段式，但超过 200px 侧栏极易截断，故默认两段）。
- `title` tooltip 全量：`创建于 2026-10-12 14:32:05 · 最后消息 2026-10-14 21:10:44 · 共 12 条消息`。

### 时间格式化函数（新建 `src/Web.Client/src/utils/timeFormat.ts`）

```ts
export function formatSessionTime(iso: string): string {
  const d = new Date(iso); if (isNaN(+d)) return '-';
  const now = new Date(); const sameYear = d.getFullYear() === now.getFullYear();
  const md = `${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`;
  return sameYear ? md : `${d.getFullYear()}-${md}`;
}
export function formatRelativeTime(iso: string): string {
  const t = new Date(iso).getTime(); if (isNaN(t)) return '-';
  const s = Math.max(0, (Date.now() - t) / 1000);
  if (s < 60) return '刚刚';
  if (s < 3600) return `${Math.floor(s/60)}分钟前`;
  if (s < 86400) return `${Math.floor(s/3600)}小时前`;
  if (s < 86400*7) return `${Math.floor(s/86400)}天前`;
  return formatSessionTime(iso);
}
export function sessionTimeTitle(s: SessionMetadata): string {
  return `创建于 ${toLocale(s.createdAtUtc)} · 最后消息 ${toLocale(s.updatedAtUtc)} · 共 ${s.messageCount} 条消息`;
}
```

- 不引入新依赖：`date-fns@4.4.0` 已在 node_modules 但为减少包体积与中文 locale 包袱，手写 20 行即可；如团队偏好 date-fns，可用 `formatDistanceToNow(zhCN)` 等价替换。
- 时区：`createdAtUtc` 为 UTC ISO，`new Date()` 自动转本地展示，tooltip 注明本地时间即可；无需 UTC 原样输出。

## 4. 施工步骤

1. 新建 `utils/timeFormat.ts`（§3 三函数 + 单测友好的纯函数）。
2. `WorkspaceSessionSidebar.vue`：
   - `script` 引入 `formatSessionTime/formatRelativeTime/sessionTimeTitle`，删 `formatTokens`（若 Tokens 下线；若保留则留着）。
   - 置顶 L223-227 与常规 L297-301 两处第二行替换为 §3 模板（建议抽 `<SessionMetaLine :sess>` 子组件或共用 `v-slot`，至少保证两处从同一函数取值，杜绝后续只改一处）。
   - 删 `Hash` 图标 import（`Hash` L372 仅 dropdown `copy-hash` 在用则保留，否则一并清理）；**保留 dropdown「复制会话 Hash」菜单项**（L479-483）——展示下线不等于能力下线，网关亲和排障仍需复制。
   - 搜索 `workspaceStore.ts` L44-53 保持 `sessionHash` 可搜（注释说明），不删。
3. 宽度验证：第二行加 `truncate` + 父级已有 `min-w-0 flex-1`，`gap-1.5` 比原 `gap-2` 更省 4px，避免 0.16 最小侧栏下截断。

## 5. 验收清单

- [ ] 置顶组与常规组均无 `Hash:`（`grep -rn "Hash:" src/Web.Client/src/components/sidebar` 零命中；注意 `copy-hash` key 与「复制会话 Hash」文案是允许残留的，仅卡面展示清零）。
- [ ] 每卡第二行形如 `10-12 · 活跃 2分钟前`，hover 显示完整创建/活跃/条数；超长不换行、不撑宽侧栏。
- [ ] 新建会话即显示 `今天 · 活跃 刚刚`；发一条消息后「活跃」即时刷新（`updatedAtUtc` 由 store 更新，无需轮询；相对时间每分钟变旧属正常，可选 60s 定时刷新 Chilpor，已列为可选优化）。
- [ ] 暗/亮截图各一；`vue-tsc -b && vite build` 通过。

## 6. 可选优化（不列入必验）

- 60 秒 `setInterval` 刷新相对时间（`nowTick` ref 触发 computed 重算），侧栏常驻时「x分钟前」不陈旧。
- 按「最后活跃」倒序已由后端索引保证，前端不另排序；若需分组（今天/本周/更早）可后续单独立项，本次不做以免 scope 膨胀。

## 7. 风险与回退

- 零后端改动，纯展示层；回退即恢复两处 `Hash:` 行，一分钟可逆。
- 旧会话 `createdAtUtc` 缺失（极端脏数据）：函数返回 `-`，tooltip 显示 `共 N 条`，不崩。
