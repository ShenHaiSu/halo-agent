# need09-04 输入框彻底重做（粘贴感知 + 富文本图片 Tag）— 前端可施工细化版

> 前置：`01/02` **已冻结**，`03` 能力位已细化。本篇为前端施工主文档，要求按节施工即无歧义。
> 现状证据：`PromptComposer.vue` 125 行（`n-input textarea + draft + Enter/Shift+Enter + /compact + isGenerating禁用 + chat:prefill-draft`）；`agentStore.sendPrompt(text)` → `bridge.startAgentTurn(ws,sid,userText,providerId,modelId)`；`WorkbenchView` 仅承载 `MessageTimeline + PromptComposer`；CefSharp 109 禁用 `navigator.clipboard.read()` 主动拉取，必须走 `paste/drop` 手势事件。

## 1. 总览：改什么、不改什么（防施工发散）

| 改 | 不改 |
|---|---|
| `PromptComposer.vue` 重写（n-input → contenteditable + 附件条 + 警告条） | `WorkbenchView.vue` 结构不动（仍是上下两块） |
| 新增 `ComposerAttachmentTag.vue`、`utils/composerModel.ts`、`utils/clipboardImage.ts` | `MessageTimeline` 本篇不动（见 05） |
| `agentStore.sendPrompt(text, attachments?)` 加可选第二参 | 发送成功后清空、`/compact` 拦截、`Esc` 停止语义不变 |
| `hostBridge.startAgentTurn` 加可选第 6 参 `attachmentsJson` | 老调用 `startAgentTurn(ws,sid,text)` 仍可用（缺省纯文本） |

## 2. 数据模型（冻结，前后端同构，精确到 TS）

```ts
// utils/composerModel.ts（唯一事实源，纯函数，可单测）
export type ComposerSegment =
  | { kind: 'text'; text: string }
  | { kind: 'image'; attId: string };
export type AttachmentSource = 'paste' | 'drop' | 'picker';
export type AttachmentStatus = 'ready' | 'oversize' | 'invalid';
export interface ComposerAttachment {
  attId: string;            // 'att_' + 8位hex，草稿内唯一（Math.random，不进提示词，与后端 imageId 区分）
  fileName: string;         // 清洗后文件名（见 §5.1）
  mime: 'image/jpeg' | 'image/png' | 'image/webp';
  sizeBytes: number;
  width?: number; height?: number;  // Image.onload 异步回填，读不到即 undefined（不阻塞）
  objectUrl: string;        // URL.createObjectURL(file) 仅内存预览，永不发送
  file: File;               // 原始 File，发送时转 base64（见 §6）
  source: AttachmentSource;
  status: AttachmentStatus;
  error?: string;           // oversize/invalid 原因，前端显示用
}
export interface ComposerDraft {
  segments: ComposerSegment[];                 // 富文本真相
  attachments: Record<string, ComposerAttachment>; // attId → 附件
}
```

**不变量（施工必须保证，联调抽查）：**
- I1：`segments` 中每个 `image` 的 `attId` 必在 `attachments` 中存在；`attachments` 中每个 `ready` 附件必被 ≥1 个 `image` 段引用（删除 pill 时同步删段；删除段时若附件无引用则同步删附件并 `revokeObjectURL`）。
- I2：序列化 `toSendPayload(draft)` 输出 `{ text, attachments }`，`text` 中 `[image:<attId>]` 占位符与 `attachments` 数组 1:1（§6）。
- I3：`draft` 为空判定 = 去掉图片占位符后的文本 `trim()===''` 且无 `ready` 附件（纯 invalid/oversize 附件不算可发送）。

## 3. 文件清单（精确到路径与职责）

| 文件 | 职责 | 禁止事项 |
|---|---|---|
| `src/Web.Client/src/utils/clipboardImage.ts` | `paste/drop` 解析纯函数：`extractPaste(e) → { files: File[], text: string, html: string }`；`acceptImageFile(f) → {ok, mime?, reason?}` 白名单校验 | 禁止在此文件碰 DOM/Store（可单测） |
| `src/Web.Client/src/utils/composerModel.ts` | 段模型操作：`insertText/insertImage/removeAttachment/serialize/deserialize/isEmpty/cleanupObjectUrls` 全纯函数 + `safeFileName()` | 禁止在此文件调 bridge（可单测） |
| `src/Web.Client/src/components/chat/ComposerAttachmentTag.vue` | pill 展示组件（props: attachment；emit: remove） | 禁止在此组件改 draft（只 emit） |
| `src/Web.Client/src/components/chat/PromptComposer.vue` | 重写：contenteditable + 事件 + 附件条 + 警告条 + 发送 | 禁止引 quill/tiptap 等新依赖 |
| `src/Web.Client/src/stores/agentStore.ts` | `sendPrompt(text, attachments?)` 扩展 | 禁止改分桶/事件分支存量逻辑 |
| `src/Web.Client/src/bridge/hostBridge.ts` | `startAgentTurn` 加第 6 可选参 + Mock 回放 | 禁止改存量 DTO 字段名 |

## 4. 粘贴/拖拽/选择（三入口统一，精确到事件）

### 4.1 `paste`（`PromptComposer.vue @paste="onPaste"`）

```ts
function onPaste(e: ClipboardEvent): void {
  if (agentStore.isGenerating || askMasked.value) { e.preventDefault(); return; }
  const cd = e.clipboardData; if (!cd) return;
  const files = collectImageFiles(cd);   // items[type^='image/'] + files[type^='image/']，见 clipboardImage.ts
  const text = cd.getData('text/plain') || '';
  const html = cd.getData('text/html') || '';
  if (files.length > 0) {
    e.preventDefault();
    for (const f of files) addFile(f, 'paste');
    if (text.trim()) insertTextAtCaret(text);  // 微信截图带说明不丢字
    return;
  }
  if (/<img/i.test(html)) {
    // 网页复制图片：data: URL 转附件；file:// 取不到字节，只插文件名提示拖入
    if (!tryHandleHtmlImage(html)) { /* 兜底走原生粘贴 */ }
    return;
  }
  // 纯文本：走原生粘贴，但超 20000 字符截断并 toast（防误贴大日志撑爆输入框）
}
```

- 禁用 `navigator.clipboard.read()`（CefSharp 109 需权限弹窗，禁止使用）。
- QQ/微信截图实测 = `image/png` file 项 + 空 text，走 `files.length>0` 分支。

### 4.2 `drop` + picker

- 容器 `@dragover.prevent`（加 `drag-over` 高亮边框类）→ `@drop.prevent="onDrop"` → `addFile(f,'drop')`。
- 工具条按钮 `🖼 添加图片`（`n-button secondary tiny`，`data-testid="composer-add-image"`）→ 隐藏 `<input type="file" accept=".jpg,.jpeg,.png,.webp" multiple>` → `addFile(f,'picker')`。
- `addFile` 统一：`acceptImageFile` 校验 → 生成 `attId` + `objectUrl` → 插入 `image` 段到光标处 → 异步读宽高回填（失败忽略）。

## 5. 富文本渲染（精确到 DOM 与 CSS）

### 5.1 结构（contenteditable 替代 n-input）

```html
<div class="composer-box">
  <div ref="editorRef" class="composer-editor" contenteditable="true"
       data-testid="composer-rich-input" aria-label="任务指令输入框，支持粘贴图片"
       @input="onEditorInput" @paste="onPaste" @drop="onDrop" @dragover="onDragOver"
       @keydown="onKeydown" @beforeinput="onBeforeInput" />
  <!-- pill 由 Vue 渲染为 editor 内的 span（contenteditable=false），见 5.2 -->
  <div class="composer-warn" v-if="visionWarn">…黄字…</div>
  <div class="composer-bar">…发送/停止按钮（存量语义）…</div>
</div>
```

- pill 节点：`<span class="img-tag" contenteditable="false" data-att="attId"><img/><span class="fname"/><button class="x">×</button></span>`，封装为 `ComposerAttachmentTag.vue`（props `attachment`，emit `remove(attId)`，`data-testid="att-remove-<attId>"`）。
- 为什么 pill 用 `contenteditable=false`：光标自动跳过 pill；`Backspace` 紧贴 pill 时 `onBeforeInput(deleteContentBackward)` 拦截删附件而非删字（必须实现，否则删图要删两次）。
- `Enter` 发送 / `Shift+Enter` 换行：`onKeydown` 内 `Enter&&!shift → preventDefault + handleSend()`；contenteditable 的换行统一转为 `\n` 进段模型（`<br><div>` 差异在此归一）。

### 5.2 样式约束（chrome109 安全子集）

- pill：`inline-flex items-center gap-1 rounded-md border px-1.5 py-0.5 text-xs` + 32px 缩略图（`w-8 h-8 object-cover rounded`）+ 文件名超 12 字 `max-w-[120px] truncate` + `×` 用 `n-button text tiny`（hover 红）。
- 禁用 `:has()`、容器查询、`color-mix`（chrome109 不支持）；只用 Tailwind v3.4 + Naive 已验证类。
- 非法附件红框：`.img-tag.invalid{border-color:rose}` + `title=error` hover 提示。

### 5.3 状态机（施工无歧义）

```
editor ⇄ draft(segments+attachments) 单向同步：
  用户输入/粘贴/删pill → 更新 draft → 全量重渲染 editor 内 pill（文本节点保留原生，用 innerHTML 只重建 pill span，光标按“段索引+偏移”恢复）
  发送成功 → draft=empty + editor.innerHTML='' + revoke全部objectUrl
  切换会话 → 按 sessionId 存取草稿（Map；图片 File 不跨会话，只存 {fileName,mime} + “需重新添加”占位pill，status=invalid,error='切换会话后需重新添加图片'）
  ask_user遮罩（见 06）→ editor contenteditable=false + 灰遮罩条，draft 保留不丢
```

## 6. 发送载荷（冻结，精确到序列化）

```ts
// composerModel.ts
export interface SendPayload { text: string; attachments: Array<{ attId: string; fileName: string; mime: string; base64: string }> }
export function toSendPayload(d: ComposerDraft): SendPayload {
  // text：text段原文 + image段→`[image:<attId>]`；attachments：仅 ready 且被引用的附件
}
async function fileToBase64(f: File): Promise<string> { /* FileReader.readAsDataURL → 取逗号后半段 */ }
```

- `agentStore.sendPrompt(text: string, attachments?: SendPayload['attachments'])`：第二参可选（老调用不变）；内部 `bridge.startAgentTurn(ws,sid,text,providerId,modelId, attachments?.length? JSON.stringify(attachments):'')`。
- 发送前规则（按顺序）：① 空草稿（I3）→ 不发送；② 只含 invalid/oversize → toast 提示并只发文字（若文字也空则不发）；③ 超 `maxImagesPerTurn` → 取**最新 N 个 ready 附件**（与后端网关一致，取新丢旧）+ 黄字已提前提示；④ 非视觉模型含图 → 按 03 §4 黄字 + 一次确认。
- 后端落盘（05）：`attachmentsJson` → `sessions/<ws>/<sess>/attachments/<date>/<attId>_<safeName>`，占位符重写为持久引用。`safeFileName`：去 `../\:*?"<>|` + 超 64 字截断 + 空名兜底 `pasted-<time>.png`。

## 7. Mock 与联调（无后端可验）

- `createBrowserMockBridge().startAgentTurn(ws,sid,text,providerId,modelId,attachmentsJson?)`：解析附件数，回放 `IterationStarted + TextDelta(含 N 张图片 Mock) + TurnCompleted`，保证芯片→发送→时间轴全链路在浏览器可演示。
- `data-testid`：`composer-rich-input / composer-add-image / composer-send / att-remove-* / composer-vision-warn`。

## 8. 施工顺序（本篇内）

```
1. clipboardImage.ts + composerModel.ts（纯函数 + 单测可跑）
2. ComposerAttachmentTag.vue（静态 pill + ×）
3. PromptComposer.vue 重写（先文本+发送跑通，再接粘贴/拖拽/附件条/警告条/遮罩）
4. agentStore + hostBridge 第6参 + Mock
5. 联调 H3/H4（见 08）
```

## 9. 边角表（本篇必测）

| # | 边角 | 预期 |
|---|---|---|
| E1 | 粘贴 gif/bmp | 拒绝 + toast `仅支持 jpg/png/webp，请转换后发送`，不建 pill |
| E2 | 单张 >8MB | 建红框 pill（oversize），发送自动跳过 + 黄字提示，不阻塞文字 |
| E3 | 粘贴 5 张（超默认4） | 暂存5张，发送取最新4张 + 黄字 |
| E4 | Backspace 紧贴 pill | 整块删附件（一次），不留悬空占位符 |
| E5 | 发送中粘贴 | `preventDefault` + 无写入（isGenerating 互斥） |
| E6 | 切换会话再切回 | 文字草稿保留；图片变“需重新添加”占位（File 不跨会话） |
| E7 | `/compact` 带图草稿 | 仍拦截为压缩命令（图片忽略，文字 `/compact` 优先） |
