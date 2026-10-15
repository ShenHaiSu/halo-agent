# 01 · 会话卡时间行“没展示出来”排查与修复方案

## 1. 现状（已读代码）

- 模板两处（置顶 L226-230 / 常规 L319-323）**已有时间行**，与 need02-03 方案一致：
```html
<div class="flex items-center gap-1.5 text-[10px] text-secondary font-mono mt-0.5 truncate" :title="sessionTimeTitle(sess)">
  <span>{{ formatSessionTime(sess.createdAtUtc) }}</span>
  <span class="opacity-50">·</span>
  <span>活跃 {{ formatRelativeTime(sess.updatedAtUtc) }}</span>
</div>
```
- 工具函数 `utils/timeFormat.ts` 全套就绪（`formatSessionTime/formatRelativeTime/sessionTimeTitle/toLocale`，非法返回 `-`）。
- 数据源 `SessionMetadata.createdAtUtc/updatedAtUtc`（`hostBridge.ts` L272-286），后端 `WorkspaceSessionService` 建会话/追加消息均维护 `UpdatedAtUtc`，DTO 经 `normalizeBridgePayload` 转 camelCase。
- 即：**代码写了时间行，但用户视野里看不见** → 本篇是排查型方案，先定位再修，不盲目重写。

## 2. 根因候选（按概率排序，施工时逐项排除）

| 优先级 | 假设 | 验证方法 | 概率 |
|---|---|---|---|
| P0 | 数据为空/非法 → 函数返回 `-` 或 `Invalid Date`，用户误认“没展示” | DevTools 看 `sessions[0].createdAtUtc`；或在时间行旁临时 `{{ sess.createdAtUtc }}` | 高：旧会话 `meta.json/sessions-index.json` 可能缺字段；C# `DateTime` 序列化格式与 `new Date()` 解析差异 |
| P1 | `text-secondary` 在当前主题下对比度过低（暗黑 `#8b949e` 10px + 侧栏窄 0.16 时被截断只剩 `·`） | 暗/亮各截一图，取色测对比度；把侧栏拉到 0.35 看是否出现 | 高 |
| P2 | `truncate + flex` 截断：父级 `min-w-0` 缺失或 `group-hover` 操作区挤占，时间行被挤出可视区 | 检查卡片 `flex items-center justify-between`，子 `min-w-0 flex-1` 是否生效；窄窗复现 | 中 |
| P3 | 相对时间不刷新：`formatRelativeTime` 是纯函数、无 `nowTick`，常驻侧栏“刚刚”永不变更，用户以为是静态占位 | 切会话/刷新后观察是否变化 | 中 |
| P4 | `title` tooltip 被 `select-none` / CefSharp 右键屏蔽误认为无时间 | hover 3s 看 tooltip | 低 |

## 3. 设计：先诊断、后加固（不改变 need02 文案共识）

- 文案保持 `MM-DD · 活跃 x分钟前`（今天/7天内相对、跨年绝对），tooltip 全量本地时间+条数。**不换文案**，只修“看不见”。
- 加固三件套：
  1. **数据兜底**：`formatSessionTime/formatRelativeTime` 对空/非法返回 `-` 已有；模板再加 `v-if` 兜底——若两端都 `-` 则显示 `时间未知` 而不是空白行（空白行 = 用户视角“没展示”）。
  2. **可读性**：时间行 `text-[10px] text-secondary` 在侧栏窄时对比度不足 → 字号提至 `11px`、间距 `gap-1.5` 不变，加 `data-testid="session-meta-line"` 供自动化断言“非空”。
  3. **即时性**：加 60s `nowTick` 定时器触发相对时间重算（`computed` 依赖 `nowTick.value`），`onUnmounted` 清理；`appendMessage/selectSession` 后 `updatedAtUtc` 已是 ISO 即时刷新，无需轮询后端。

## 4. 施工步骤

1. **复现定位**（必做，不写代码先看）：
   - 浏览器/Cef DevTools 执行 `JSON.parse(JSON.stringify(sessions[0]))`，确认 `createdAtUtc/updatedAtUtc` 是否非空 ISO；若空 → 查后端 `SessionRepository.LoadSessionsIndexAsync` 旧数据补默认值（`CreatedAtUtc == default → DateTime.UtcNow`），并在 `getSessions` 后前端再做一次空值回填（不改 DTO）。
   - 侧栏拉到最小 0.16 与最大 0.35 各截一图，确认是“真没有 DOM”还是“有 DOM 但看不见”（Elements 面板搜 `session-meta-line`）。
2. `WorkspaceSessionSidebar.vue`：
   - 两处时间行加 `data-testid="session-meta-line"`；文本后备：`{{ formatSessionTime(...) || '时间未知' }}`（函数已返 `-`，模板判 `-` 即显示 `时间未知`，避免空白）。
   - `script` 加 `const nowTick = ref(Date.now()); let tickTimer; onMounted(() => tickTimer = setInterval(() => nowTick.value = Date.now(), 60000)); onUnmounted(清理)`；`formatRelativeTime` 调用处无改（重渲染靠 `nowTick` 被模板读取——在时间行插值旁加隐藏依赖或把 `nowTick` 传入 computed 包装 `relativeLabel(sess)`）。
   - 若 P2 成立：确认父级已有 `min-w-0 flex-1`（L205/L298 有），时间行追加 `min-w-0` + `overflow-hidden`，操作区 `shrink-0` 保留。
3. 脏数据兼容：`timeFormat.ts` 不动（已完备）；若后端旧 `meta.json` 缺时间，前端 `loadSessions` 后对缺失项填 `new Date().toISOString()` 仅内存态展示，不回写磁盘（避免污染排序）。
4. 不动搜索（`workspaceStore` L44-53 保留 hash 可搜）、不动 dropdown 复制 hash 项。

## 5. 验收清单

- [ ] Elements 中每卡都有 `data-testid="session-meta-line"` 且文本非空（`[...document.querySelectorAll('[data-testid=session-meta-line]')].every(el => el.textContent.trim().length > 0)`）。
- [ ] 新建会话显示 `MM-DD · 活跃 刚刚`；发一条消息后“活跃”即时刷新，无需切卡。
- [ ] 常驻 2 分钟后“刚刚”→“x分钟前”自动变化（60s tick 生效）。
- [ ] 0.16 最小侧栏下时间行单行截断、不换行、不撑宽；hover 显示 `创建于 … · 最后消息 … · 共 N 条消息`。
- [ ] 非法时间（空串/`null`）显示 `时间未知`，不白屏不崩；`vue-tsc -b && vite build` 通过；暗/亮截图各一。

## 6. 风险与回退

- 纯展示层 + 只读兜底，无后端改动；回退即移除 `nowTick` 与 `data-testid`，恢复原两行模板，一分钟可逆。
- 60s 定时器仅触发侧栏重渲染，开销可忽略；若性能敏感可降为 120s 或仅当侧栏可见时 tick。
