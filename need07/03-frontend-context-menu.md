# need07-03 前端：右键菜单与编排（Menu + Composer + Store）

> 前置：`01` 语义冻结、`02` 接口冻结。本篇精确到文件与行级改法。

## 1. 设计一句话

`MessageTimeline` 只负责**锚点定位 + 菜单摆位**，新组件 `MessageContextMenu` 只负责**纯展示**，真正的“截断 → 复发 / 分叉 → 切卡 → 草稿预填”编排下沉到 `workspaceStore/agentStore`，`PromptComposer` 只订阅 `chat:prefill-draft` 事件写草稿。剪切板走双通道，菜单走自研浮层（不引入新依赖，兼容 CefSharp Chrome 109）。

## 2. 文件清单（只动 6 处，新增 2 处）

| 动作 | 文件 | 改法 |
|------|------|------|
| 新增 | `components/chat/MessageContextMenu.vue` | 纯展示浮层：props `x/y/visible/disabledReason/targetKind`，emit `select(action)`；含 `data-testid`；暗亮用 `main.css` 语义变量 |
| 改 | `components/chat/MessageTimeline.vue` | user 气泡 + turn 正文容器加 `@contextmenu.prevent`；维护 `menuState`；挂载 `<MessageContextMenu>`；监听全局 click/Esc/滚动关菜单 |
| 改 | `components/chat/PromptComposer.vue` | 订阅 `haloEventBus.on('chat:prefill-draft')` 写 `draft` + focus；分支切卡后自动聚焦 |
| 改 | `stores/workspaceStore.ts` | 新增 `truncateHistory(anchorId, removeAnchor)`、`branchFromMessage(anchorId, includeAnchor)`（调 bridge + 切卡 + 发预填事件） |
| 改 | `stores/agentStore.ts` | 新增 `replayUserMessage(msgId)`、`replayAssistantTurn(assistantId)`（串行：truncate → reload → sendPrompt） |
| 改 | `bridge/hostBridge.ts` | `HostBridgeApi` 加 `truncateSessionHistory/branchSession`；真机 `wrapNativeHostBridge` 转发；Mock 内存实现同语义（含轮次展开） |
| 新增 | `utils/historyAnchor.ts` | 前端切面纯函数：`expandAssistantEnd(msgs, idx)`、`findPrevUser(msgs, idx)`、`copyTextToClipboard(text)`（供 Menu/Timeline/store 复用 + 单测） |

## 3. 交互细节（照着写）

### 3.1 `MessageTimeline.vue` 锚点

```vue
<!-- user 气泡 -->
<div @contextmenu.prevent="openMenu($event, { kind:'user', id: item.msg.id, text: item.msg.content })" ...>
<!-- turn 正文容器 -->
<div @contextmenu.prevent="openMenu($event, { kind:'assistant', id: item.group.assistantId, text: item.group.content })" ...>
```

- `openMenu(e, target)`：若空文本仍可开菜单（复制项置灰，见 E9）；记录 `menuState={x:e.clientX,y:e.clientY,target}`；`visible=true`。
- 视口钳制：`x=min(x, innerWidth-200)`，`y=min(y, innerHeight-140)`，菜单 `position:fixed`。
- 关闭：全局 `click/scroll(resize)/Esc` 关；切会话（`activeSessionId` 变化）关。
- 禁用态：`const busy = agentStore.isGenerating || agentStore.streamingActive || agentStore.isCompacting(activeSession)`；busy 时重放/分支项 `disabled` + `title="生成中不可操作"`，复制保持可用。

### 3.2 `MessageContextMenu.vue` 展示

- 三行：`复制`（Copy）/ `重放`（RotateCcw）/ `从这里分支`（GitBranch）；lucide 图标与现有 `BrainCircuit` 同风格，`w-3.5 h-3.5`。
- 样式：`rounded-lg border border-subtle bg-surface shadow-xl text-xs`，hover `bg-elevated`，disabled `opacity-40 cursor-not-allowed`；`z-50`；宽 `w-44`；不引入 `n-dropdown`（NAIVE 2.45 在 CEF 下定位抖动，自研最稳）。
- `data-testid="message-context-menu"`，三项分别为 `context-menu-copy|replay|branch`。

### 3.3 复制（纯前端，不经后端）

```ts
// utils/historyAnchor.ts
export async function copyTextToClipboard(text: string): Promise<boolean> {
  if (!text) return false;
  try { await navigator.clipboard.writeText(text); return true; }
  catch {
    try { // CefSharp 降级
      const ta = document.createElement('textarea');
      ta.value = text; ta.style.position='fixed'; ta.style.opacity='0';
      document.body.appendChild(ta); ta.select();
      const ok = document.execCommand('copy'); ta.remove(); return ok;
    } catch { return false; }
  }
}
```

成功 `window.$message?.success('已复制')`（无 message 挂载则 `console.info` 兜底，保持零依赖）。

### 3.4 重放编排（`agentStore`）

```ts
async function replayUserMessage(msgId: string): Promise<boolean> {
  const ws = workspaceStore(); const sid = ws.activeSessionId; if (!sid || busy(sid)) return false;
  const target = ws.sessionMessages.find(m => m.id === msgId); if (!target) return false;
  const text = (target.content || '').trim(); if (!text) return false;
  const res = await bridge.truncateSessionHistory(ws.activeWorkspaceId, sid, msgId, true);
  if (!res.ok) { toast(res.message); return false; }
  await ws.loadSessionMessages(sid); await ws.refreshBudget();
  return await sendPrompt(text);          // 复发：重建单条 user + 新一轮推理
}
async function replayAssistantTurn(assistantId: string): Promise<boolean> {
  // 前驱 user 由后端在 truncate 内定位并返回 replayText；前端只需传 assistantId + removeAnchor=true，
  // 后端按“前驱 user 切面”截断 → 前端用 res.replayText 复发。后端返回 NO_PRIOR_USER 则吐司。
}
```

要点：后端 `removeAnchor` 语义统一为“重放模式”（内部按前驱切面），前端不做索引算术——**切面以后端为准**，前端 `sessionMessages` 仅用于取文案做忙态前置校验与错误提示。

### 3.5 分支编排（`workspaceStore`）

```ts
async function branchFromMessage(anchorId: string, kind: 'user'|'assistant'): Promise<boolean> {
  const includeAnchor = kind === 'assistant';
  const anchor = sessionMessages.value.find(m => m.id === anchorId
    || (kind==='assistant' && m.id===anchorId));           // assistant 锚即 assistant msg id
  const draftText = kind === 'user' ? String(anchor?.content || '') : '';
  const res = await bridge.branchSession(activeWorkspaceId.value, activeSessionId.value, anchorId, includeAnchor);
  if (!res.ok || !res.data) return false;
  sessions.value = [res.data, ...sessions.value];
  await selectSession(res.data.id);                        // 切卡 + 重拉消息 + 预算
  haloEventBus.emitLocal('chat:prefill-draft', { text: draftText, sessionId: res.data.id });
  return true;
}
```

`PromptComposer` 订阅后仅当 `sessionId` 等于当前会话才写入，避免后台切卡污染：

```ts
haloEventBus.on('chat:prefill-draft', (p) => {
  if (p.sessionId !== workspaceStore.activeSessionId) return;
  draft.value = p.text || ''; focusInput();
});
```

### 3.6 Mock 对齐（`hostBridge.createBrowserMockBridge`）

`mockMessages` 上实现同语义内存版：轮次展开 + 前驱查找 + 切片 + 新会话 `mockSessions.unshift + mockMessages[newId]=prefix`，供 Web 调试零后端联调。

## 4. 样式与兼容（CefSharp Chrome 109 铁律）

- 只用 `var()/flex/grid`；禁用 `:has()/container/color-mix()`；菜单阴影用 `box-shadow`  token（`--shadow-menu` 无则用 `0 8px 24px rgba(0,0,0,.35)`）。
- 暗亮：背景 `bg-surface`、边框 `border-subtle`、文字 `text-primary/secondary`，与气泡同 token。
- 防横溢：时间轴容器已有 `overflow-x-hidden`；菜单 `max-width:11rem` + `word-break:break-all`；`x/y` 钳制后不再出视口。

## 5. 验收（本篇）

- [ ] 右键 user/assistant 均出菜单，定位跟手，滚动/点空/Esc/切卡即关。
- [ ] 生成中重放/分支置灰不可点，复制可点。
- [ ] 复制原文逐字一致（含换行/code fence），E9/E10 按 01 表格表现。
- [ ] 重放后时间轴先回退再流式重出，无双 user；分支后侧栏顶部新卡 + 切卡 + 草稿箱（user 有文可编辑发送 / assistant 空）。
- [ ] `vue-tsc -b && vite build` 全绿；Mock 下同上全过。
