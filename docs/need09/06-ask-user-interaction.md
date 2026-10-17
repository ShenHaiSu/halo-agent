# need09-06 ask_user 阻塞式问询工具 — 前端可施工细化版

> 前置：`01/02` **已冻结**，`04` 输入框已细化。本篇后端阻塞语义冻结不变，前端 modal 细化到可施工。
> 现状证据：`AgentOrchestrator` ReAct 循环 + `CancellationToken` 取消；`ToolRegistry.DispatchAsync` 非阻塞转自愈；`agentStore` 按 session 分桶 + `isBusyForHistory` + `haloEventBus(agent:turn-event/llm:stream-event)`；04 后输入框为 contenteditable（含 `masked` 扩展位）。

## 1. 语义与载荷（冻结回顾，施工对照）

- 工具名 `ask_user`；模型传 `{questions:[{id,question,options?[{label,description?}],multiSelect?,allowCustom?,default?}]}`（1–4 题，选项 0–6 个，空 options 即开放问答）；顶层允许 `{questions}` 或直接数组；`question` 单数别名兼容。
- YAML 糖：后端最小子集解析（`key: value` + `- ` + `|` 块 + `#` 注释），失败转自愈 JSON 模板。
- 返回：`{answers:[{id,selected[],customInput,skipped}]}` + 文本摘要 `用户已回答（2/2）：[framework] xxx；…`（tool 消息 Content，超 500 字截断，JSON 放 ```json 块）。
- 后端：`AskUserTool + AskUserGate(sessionId→单pending)`，`JsEventDispatcher.Publish("agent:ask-user-requested")` → `await ticket(TaskCompletionSource).WaitAsync(askUserTimeoutSec默认300s, ct)`；超时/取消转 Fail 不挂起；第二并发 `ask_user` 直接 Fail。

## 2. 前端文件清单（精确到路径）

| 文件 | 职责 | 禁止事项 |
|---|---|---|
| `components/chat/AskUserModal.vue` | 新增：`n-modal + n-steps + 选项卡`（本篇 §3） | 禁止在 modal 内直接调 `sendPrompt`（只经 `resolveAskUser`） |
| `stores/askUserStore.ts` | 新增：按 session 分桶的问询态（`pendingBySession{sessionId:{ticketId,callId,questions,arrivedAt}}` + 当前 modal 答案草稿） | 禁止复用 `agentStore.turns` 桶（问询态独立，避免轮末清空误删） |
| `components/chat/PromptComposer.vue` | 扩展：`masked` 计算（`askUserStore.hasPending(activeSession)`）→ `contenteditable=false` + 灰遮罩条（§4） | 禁止丢 draft（遮罩只禁用不清空） |
| `bridge/hostBridge.ts` | 新增 `resolveAskUser(sessionId,ticketId,answersJson)` + 事件透传（`agent:ask-user-requested/resolved` 经存量 `haloEventBus`，不新增通道）+ Mock 假答案 | 禁止改存量事件名 |
| `components/sidebar/WorkspaceSessionSidebar.vue` | 扩展：后台会话待回答红点（`askUserStore.pendingCount(sessionId)` → badge） | 禁止后台弹窗抢焦点 |

## 3. `AskUserModal.vue`（精确到 DOM 与交互）

### 3.1 触发与归属（无歧义）

```ts
haloEventBus.on('agent:ask-user-requested', (p: { sessionId; ticketId; callId; questions }) => {
  askUserStore.putPending(p);  // 按 sessionId 落桶（含 arrivedAt）
  if (p.sessionId !== workspaceStore.activeSessionId) return; // 后台只红点不弹窗（§5）
  askUserStore.openModal(p.sessionId);  // 前台才弹窗
});
haloEventBus.on('agent:ask-user-resolved', (p: { sessionId; ticketId; outcome }) => {
  askUserStore.clearPending(p.sessionId, p.ticketId);  // 成功/超时/取消统一解罩
});
```

- 切换会话：modal 内容跟随当前会话的 pending（`computed currentPending = pendingBySession[activeSession]`）；无 pending 自动关闭（避免 A 问卷留在 B 会话）。
- 关闭路径只有三条：提交回答 → `resolveAskUser`；点“取消提问”/右上× → `cancelAgentTurn`（等价取消整轮，后端走取消分支）；后端超时/取消事件 → 自动关闭 + toast。

### 3.2 布局（Naive 已验证组件，1020px 无横溢）

```
<n-modal v-model:show="visible" :mask-closable="false" :close-on-esc="false" preset="card"
         title="需要你的确认" style="width:560px;max-width:94vw" data-testid="ask-user-modal">
  <n-steps :current="step" size="small"> <n-step v-for="(q,i)" :title="`问题${i+1}`" /> </n-steps>
  <div class="q-card" v-for="(q,i) in questions" v-show="i===step-1">
    <div class="q-title">{{ q.question }}</div>
    <n-radio-group v-if="!q.multiSelect && q.options?.length" v-model:value="draft[q.id].selected[0]">…</n-radio-group>
    <n-checkbox-group v-if="q.multiSelect" v-model:value="draft[q.id].selected">…</n-checkbox-group>
    <n-input v-if="q.allowCustom" v-model:value="draft[q.id].customInput" type="textarea"
             :autosize="{minRows:2,maxRows:4}" placeholder="或手动输入自定义答案…" :data-testid="`ask-user-custom-${q.id}`" />
    <n-button text tiny @click="skip(q.id)" :data-testid="`ask-user-skip-${q.id}`">跳过此题</n-button>
  </div>
  <template #footer>
    <n-button @click="prev" :disabled="step<=1">上一步</n-button>
    <n-button @click="next" v-if="step<questions.length">下一步</n-button>
    <n-button type="primary" @click="submit" data-testid="ask-user-submit">提交回答（{{ answered }}/{{ total }}）</n-button>
    <n-button type="error" ghost @click="cancel">取消提问</n-button>
  </template>
</n-modal>
```

- 选项渲染：`n-radio/n-checkbox` 的 label + `description` 灰字（`value=label` 原样回传，禁止前后空格截断以外的改写）。
- 开放问答（无 options）：只显示自定义输入框 + 跳过。
- 校验：单选未选且无自定义 → 记 `skipped:true`（不阻塞提交）；自定义 ≤2000 字（超限 toast 并截断）；多选空选同理记 skipped。
- 提交 payload（精确到字段）：`{answers: questions.map(q => ({ id: q.id, selected: draft[q.id].selected.filter(Boolean), customInput: draft[q.id].customInput.trim().slice(0,2000), skipped: 是否未选且无输入 }))}` → `JSON.stringify` → `bridge.resolveAskUser(sessionId, ticketId, json)`。
- `data-testid`：`ask-user-modal / ask-user-option-<qid>-<idx> / ask-user-custom-<qid> / ask-user-skip-<qid> / ask-user-submit / ask-user-cancel`。

### 3.3 样式约束

- 宽度 `560px/max-94vw`，问题卡 `max-h-[50vh] overflow-y-auto`（长选项不撑出屏幕）；禁用 `:has()`/容器查询；只用 `n-modal/n-steps/n-radio/n-checkbox/n-input/n-button`。

## 4. 输入框遮罩（`PromptComposer.vue` 扩展，精确到条件）

```ts
const askMasked = computed(() => askUserStore.hasPending(workspaceStore.activeSessionId));
watch(askMasked, v => { if (editorRef.value) editorRef.value.contentEditable = v ? 'false' : 'true'; });
```

- 遮罩 UI：editor 置灰（`opacity-60 pointer-events-none`）+ 顶部灰条 `等待回答问题…（已遮罩输入框）` + 发送按钮 `:disabled="askMasked || isGenerating"`。
- `onPaste/onDrop/handleSend` 首行加 `if (askMasked.value) return`（与 04 §4.1 一致）。
- 解罩：`agent:ask-user-resolved` 或 `TurnCompleted/Cancelled` 后 `clearPending` → 自动恢复 `contenteditable=true`，draft 原样保留。

## 5. 后台会话与忙态（精确到行为）

- 后台 pending：`WorkspaceSessionSidebar` 会话行红点 `待回答(1)`（`data-testid="sess-ask-badge-<sid>"`）；点击切回后 modal 自动弹出（`watch(activeSessionId)` 内 `if hasPending → openModal`）。
- `isBusyForHistory(sid)`：问询 pending 视为忙（`askUserStore.hasPending(sid) → true`），重放/分支/压缩/新发送一律拒绝并 toast `请先回答问题`。
- `stopGenerating` 在问询等待期间可用（= 取消提问 + 取消整轮，后端 `ct` 联动，见 §1）。

## 6. Mock（无后端可验）

- `createBrowserMockBridge`：收到 `ask_user` 事件 1s 后自动回放一组假 `agent:ask-user-requested`（2 题：单选+开放），`resolveAskUser` 直接回 `ok:true` 并 publish `agent:ask-user-resolved(outcome=submitted)`。保证 modal 全链路浏览器可演示。
- Mock `resolveAskUser` 入参校验：`ticketId` 对不上返回 `ok:false,code:ASK_TICKET_MISMATCH`（与真后端一致）。

## 7. 施工顺序（本篇内）

```
1. hostBridge.resolveAskUser + 事件透传 + Mock
2. askUserStore（分桶 + openModal/clearPending + 草稿）
3. AskUserModal.vue（先单题提交跑通，再多题 steps/跳过/后台红点）
4. PromptComposer 遮罩 + isBusyForHistory 联动
5. 联调 H7（见 08）+ E1–E5
```

## 8. 边角表（本篇必测）

| # | 边角 | 预期 |
|---|---|---|
| E1 | 模型传 5 题 | 后端拒绝不弹窗，前端无残留 pending |
| E2 | 右上×/取消提问 | =取消整轮（`cancelAgentTurn`），后端 Fail，模型保守继续 |
| E3 | 后台会话提问 | 不弹窗只红点，切回自动弹 |
| E4 | YAML 锚点/中文冒号 | 后端自愈 JSON 模板，前端无感 |
| E5 | 等待时点停止生成 | 提问取消+整轮取消，无残留 pending；输入框解罩 |
| E6 | 提交空答案 | 全部记 skipped 并提交（不阻塞），模型按默认/保守继续 |
| E7 | 刷新页面等待中 | 后端 300s 超时兜底解罩，不永久挂起 |
