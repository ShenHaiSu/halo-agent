<template>
  <div class="shrink-0 px-6 pb-4 pt-2" data-testid="prompt-composer">
    <div
      class="composer-box max-w-4xl mx-auto rounded-xl border border-subtle bg-surface focus-within:border-blue-500/80 focus-within:ring-2 focus-within:ring-blue-500/15 transition-all shadow-lg p-2.5 space-y-2"
      :class="dragOver ? 'drag-over' : ''"
      @dragover="onDragOver"
      @dragleave="onDragLeave"
      @drop="onDrop"
    >
      <!-- ask_user 遮罩条 -->
      <div v-if="askMasked" class="rounded-md bg-elevated border border-subtle px-2.5 py-1.5 text-xs text-secondary">
        等待回答问题…（输入框已遮罩，提交后恢复）
      </div>
      <div
        ref="editorRef"
        class="composer-editor"
        contenteditable="true"
        data-testid="composer-rich-input"
        aria-label="任务指令输入框，支持粘贴图片"
        :class="askMasked ? 'masked' : ''"
        @input="onEditorInput"
        @paste="onPaste"
        @keydown="onKeydown"
        @beforeinput="onBeforeInput"
        @click="onEditorClick"
      />
      <!-- 附件条 -->
      <div v-if="attachmentList.length" class="flex flex-wrap gap-1.5">
        <ComposerAttachmentTag
          v-for="att in attachmentList"
          :key="att.attId"
          :attachment="att"
          @remove="removeImage"
        />
      </div>
      <!-- 警告条 -->
      <n-alert
        v-if="visionWarn"
        type="warning"
        :show-icon="false"
        class="text-xs"
        data-testid="composer-vision-warn"
      >
        当前模型不支持看图，发送时将自动忽略图片（文字仍发送）
      </n-alert>
      <div v-if="overLimitWarn" class="text-[11px] text-amber-500">
        图片超限，将只发送最新 {{ maxImages }} 张
      </div>
      <div v-if="oversizeWarn" class="text-[11px] text-amber-500">
        部分图片超过 8MB，发送时将自动跳过（文字仍发送）
      </div>
      <div class="flex items-center justify-end gap-2">
        <n-button
          secondary
          size="small"
          data-testid="composer-add-image"
          :disabled="!canSend || agentStore.isGenerating || askMasked"
          @click="openPicker"
        >
          🖼 添加图片
        </n-button>
        <input
          ref="fileRef"
          type="file"
          accept=".jpg,.jpeg,.png,.webp"
          multiple
          class="hidden"
          @change="onPickerChange"
        />
        <n-button
          v-if="!agentStore.isGenerating"
          type="primary"
          size="small"
          data-testid="composer-send"
          :disabled="!canSend"
          @click="handleSend"
        >
          <template #icon>
            <SendHorizontal class="w-3.5 h-3.5" />
          </template>
          发送 (Enter)
        </n-button>
        <n-button v-else type="error" size="small" @click="handleStop">
          <template #icon>
            <Square class="w-3.5 h-3.5" />
          </template>
          停止生成 (Esc)
        </n-button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
 import { useDialog, useMessage } from 'naive-ui';
 import { SendHorizontal, Square } from 'lucide-vue-next';
 import { haloEventBus } from '@/bridge/hostBridge';
 import { useWorkspaceStore } from '@/stores/workspaceStore';
 import { useAgentStore } from '@/stores/agentStore';
 import { useConfigStore } from '@/stores/configStore';
import ComposerAttachmentTag from '@/components/chat/ComposerAttachmentTag.vue';
import {
  cleanupObjectUrls,
  createEmptyDraft,
  genAttId,
  insertImage,
  isEmpty,
  MAX_IMAGE_BYTES,
  MAX_PASTE_TEXT,
  removeAttachment,
  safeFileName,
  toSendPayload,
  type AttachmentSource,
  type ComposerAttachment,
  type ComposerDraft,
  type ComposerImageMime,
} from '@/utils/composerModel';
import { acceptImageFile, collectImageFiles, extractPaste, firstHtmlImgSrc } from '@/utils/clipboardImage';

const workspaceStore = useWorkspaceStore();
const agentStore = useAgentStore();
const configStore = useConfigStore();
const message = useMessage();
const dialog = useDialog();

const editorRef = ref<HTMLElement | null>(null);
const fileRef = ref<HTMLInputElement | null>(null);
const dragOver = ref(false);
const askMasked = ref(false);
const textLen = ref(0);

 let draft: ComposerDraft = createEmptyDraft();
 const draftVer = ref(0);
 function setDraft(next: ComposerDraft): void {
   draft = next;
   draftVer.value++;
 }

/** 会话级草稿：文字保留；图片 File 不跨会话，只存文件名/mime 作 invalid 占位 */
interface SavedDraft { text: string; images: Array<{ fileName: string; mime: ComposerImageMime }> }
const savedDrafts: Record<string, SavedDraft> = {};

const canSend = computed(() => !!(workspaceStore.activeSessionId || workspaceStore.activeSession?.id));

 const attachmentList = computed<ComposerAttachment[]>(() => { void draftVer.value; return Object.values(draft.attachments); });

const readyCount = computed(() => attachmentList.value.filter((a) => a.status === 'ready').length);

const maxImages = computed(() => {
  const raw = Number(configStore.activeModel?.maxImagesPerTurn);
  if (!Number.isFinite(raw) || raw === 0) return 4;
  return Math.max(1, Math.min(16, Math.trunc(raw)));
});

const supportsVision = computed(() => configStore.activeModel?.supportsVision === true);

const visionWarn = computed(() => readyCount.value > 0 && !supportsVision.value);
const overLimitWarn = computed(() => readyCount.value > maxImages.value);
const oversizeWarn = computed(() => attachmentList.value.some((a) => a.status === 'oversize'));

// ---------- editor DOM ----------
function escapeHtml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function shortNameOf(n: string): string {
  const arr = [...String(n || '')];
  return arr.length <= 12 ? n : arr.slice(0, 12).join('') + '…';
}

function createPillEl(att: ComposerAttachment): HTMLSpanElement {
  const span = document.createElement('span');
  span.className = 'img-tag' + (att.status === 'ready' ? '' : ' invalid');
  span.contentEditable = 'false';
  span.setAttribute('data-att', att.attId);
  span.title = att.error || att.fileName;
  if (att.objectUrl) {
    const img = document.createElement('img');
    img.src = att.objectUrl;
    img.alt = att.fileName;
    img.className = 'img-thumb';
    img.draggable = false;
    span.appendChild(img);
  }
  const fn = document.createElement('span');
  fn.className = 'fname';
  fn.textContent = shortNameOf(att.fileName);
  span.appendChild(fn);
  const btn = document.createElement('button');
  btn.type = 'button';
  btn.className = 'img-x';
  btn.textContent = '×';
  btn.setAttribute('data-testid', `att-remove-${att.attId}`);
  btn.setAttribute('data-x', att.attId);
  btn.setAttribute('aria-label', `移除图片 ${att.fileName}`);
  span.appendChild(btn);
  return span;
}

/** 编辑器 DOM → 段模型（文本保留原生；pill 按 data-att 还原 image 段） */
function readEditorToDraft(): void {
  const editor = editorRef.value;
  if (!editor) return;
  const segments: ComposerDraft['segments'] = [];
  let buf = '';
  const flush = () => {
    if (buf) {
      const last = segments.length > 0 ? segments[segments.length - 1] : null;
      if (last && last.kind === 'text') last.text += buf;
      else segments.push({ kind: 'text', text: buf });
      buf = '';
    }
  };
  const walk = (node: Node) => {
    if (node.nodeType === 3) {
      buf += node.textContent || '';
      return;
    }
    if (node.nodeType !== 1) return;
    const el = node as HTMLElement;
    if (el.nodeName === 'BR') {
      buf += '\n';
      return;
    }
    const attId = el.getAttribute && el.getAttribute('data-att');
    if (attId && draft.attachments[attId]) {
      flush();
      segments.push({ kind: 'image', attId });
      return;
    }
    if (el.nodeName === 'DIV' || el.nodeName === 'P') {
      const was = buf.length > 0 && !buf.endsWith('\n');
      void was;
      for (let i = 0; i < el.childNodes.length; i++) walk(el.childNodes[i]);
      buf += '\n';
      return;
    }
    for (let i = 0; i < el.childNodes.length; i++) walk(el.childNodes[i]);
  };
  for (let i = 0; i < editor.childNodes.length; i++) walk(editor.childNodes[i]);
  flush();
  // 尾部换行归一（contenteditable 块级差异在此收敛）
  const last = segments.length > 0 ? segments[segments.length - 1] : null;
  if (last && last.kind === 'text') last.text = last.text.replace(/\n+$/, '');
  // I1：悬空 image 段自愈
  setDraft({
    segments: segments.filter((s) => s.kind === 'text' || draft.attachments[s.attId]),
    attachments: { ...draft.attachments },
  });
  // 无引用附件回收
  gcUnreferenced();
  textLen.value = segments
    .filter((s) => s.kind === 'text')
    .map((s) => (s as { kind: 'text'; text: string }).text)
    .join('').length;
}

function gcUnreferenced(): void {
  const used = new Set<string>();
  for (const s of draft.segments) if (s.kind === 'image') used.add(s.attId);
  for (const id of Object.keys(draft.attachments)) {
    if (!used.has(id)) {
      try {
        const u = draft.attachments[id].objectUrl;
        if (u) URL.revokeObjectURL(u);
      } catch { /* ignore */ }
      delete draft.attachments[id];
    }
  }
}

function renderEditorFromText(text: string): void {
  const editor = editorRef.value;
  if (!editor) return;
  editor.innerHTML = text ? escapeHtml(text).replace(/\n/g, '<br>') : '';
  textLen.value = text.length;
}


function placeCaretAfter(node: Node): void {
  try {
    const sel = window.getSelection();
    if (!sel || !editorRef.value) return;
    const range = document.createRange();
    range.setStartAfter(node);
    range.collapse(true);
    sel.removeAllRanges();
    sel.addRange(range);
    editorRef.value.focus();
  } catch { /* ignore */ }
}

function insertTextAtCaret(text: string): void {
  const editor = editorRef.value;
  if (!editor) return;
  editor.focus();
  let done = false;
  try {
    done = document.execCommand('insertText', false, text);
  } catch {
    done = false;
  }
  if (!done) {
    try {
      const sel = window.getSelection();
      const range = sel && sel.rangeCount > 0 ? sel.getRangeAt(0) : null;
      if (range && editor.contains(range.commonAncestorContainer)) {
        range.deleteContents();
        range.insertNode(document.createTextNode(text));
        range.collapse(false);
        sel?.removeAllRanges();
        sel?.addRange(range);
      } else {
        editor.appendChild(document.createTextNode(text));
      }
    } catch { /* ignore */ }
  }
  readEditorToDraft();
}

function insertPillAtCaret(att: ComposerAttachment): void {
  const editor = editorRef.value;
  if (!editor) {
    setDraft(insertImage(draft, att.attId));
    return;
  }
  editor.focus();
  const pill = createPillEl(att);
  try {
    const sel = window.getSelection();
    const range = sel && sel.rangeCount > 0 ? sel.getRangeAt(0) : null;
    if (range && editor.contains(range.commonAncestorContainer)) {
      range.deleteContents();
      range.insertNode(pill);
      // pill 后补空文本节点，保证可继续输入
      if (!pill.nextSibling) editor.appendChild(document.createTextNode(''));
      placeCaretAfter(pill);
    } else {
      editor.appendChild(pill);
      editor.appendChild(document.createTextNode(''));
      placeCaretAfter(pill);
    }
  } catch {
    editor.appendChild(pill);
  }
  readEditorToDraft();
}

// ---------- 附件入口 ----------
function addFile(f: File, source: AttachmentSource): void {
  const chk = acceptImageFile(f);
  if (!chk.ok) {
    message.error(chk.reason || '仅支持 jpg/png/webp，请转换后发送');
    return;
  }
  const mime = chk.mime as ComposerImageMime;
  const attId = genAttId();
  const fileName = safeFileName(f.name || `pasted-${Date.now()}.png`);
  const oversize = f.size > MAX_IMAGE_BYTES;
  const att: ComposerAttachment = {
    attId,
    fileName,
    mime,
    sizeBytes: f.size,
    objectUrl: URL.createObjectURL(f),
    file: f,
    source,
    status: oversize ? 'oversize' : 'ready',
    error: oversize ? `单张超过 8MB（${(f.size / 1024 / 1024).toFixed(1)}MB），发送时自动跳过` : undefined,
  };
  draft.attachments[attId] = att;
  setDraft(insertImage(draft, attId));
  insertPillAtCaret(att);
  if (oversize) message.warning('图片超过 8MB，发送时将自动跳过（文字仍发送）');
  // 宽高异步回填，失败忽略
  try {
    const probe = new Image();
     probe.onload = () => {
       const hit = draft.attachments[attId];
       if (hit) {
         hit.width = probe.naturalWidth;
         hit.height = probe.naturalHeight;
       }
     };
    probe.onerror = () => { /* 读不到即 undefined，不阻塞 */ };
    probe.src = att.objectUrl;
  } catch { /* ignore */ }
}

function removeImage(attId: string): void {
  const hit = draft.attachments[attId];
  try {
    if (hit && hit.objectUrl) URL.revokeObjectURL(hit.objectUrl);
  } catch { /* ignore */ }
  setDraft(removeAttachment(draft, attId));
  try {
    const editor = editorRef.value;
    const pill = editor?.querySelector(`[data-att="${attId}"]`);
    pill?.parentNode?.removeChild(pill);
  } catch { /* ignore */ }
  readEditorToDraft();
  editorRef.value?.focus();
}

// ---------- paste / drop ----------
function tryHandleHtmlImage(html: string): boolean {
  const src = firstHtmlImgSrc(html);
  if (!src) return false;
  if (src.startsWith('data:image/')) {
    const comma = src.indexOf(',');
    const head = src.slice(0, comma);
    const body = comma >= 0 ? src.slice(comma + 1) : '';
    const mimeRaw = head.split(';')[0].slice(5).toLowerCase();
    const mime: ComposerImageMime | null =
      mimeRaw === 'image/png' ? 'image/png' : mimeRaw === 'image/webp' ? 'image/webp' : mimeRaw === 'image/jpeg' || mimeRaw === 'image/jpg' ? 'image/jpeg' : null;
    if (!mime || !body) return false;
    try {
      const bin = atob(body);
      const bytes = new Uint8Array(bin.length);
      for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
      const ext = mime === 'image/png' ? 'png' : mime === 'image/webp' ? 'webp' : 'jpg';
      const file = new File([bytes], `pasted-${Date.now()}.${ext}`, { type: mime });
      addFile(file, 'paste');
      return true;
    } catch {
      return false;
    }
  }
  // file:// 或远端 URL 取不到字节：只插文件名提示拖入
  try {
    const name = decodeURIComponent(String(src).split('?')[0].split('#')[0].split('/').filter(Boolean).pop() || '网页图片');
    insertTextAtCaret(`[${name} 请拖入图片文件发送]`);
  } catch {
    insertTextAtCaret('[网页图片 请拖入图片文件发送]');
  }
  return true;
}

function onPaste(e: ClipboardEvent): void {
  if (agentStore.isGenerating || askMasked.value) {
    e.preventDefault();
    return;
  }
  const { files, text, html } = extractPaste(e);
  if (files.length > 0) {
    e.preventDefault();
    for (const f of files) {
      const named = f.name ? f : new File([f], `pasted-${Date.now()}.png`, { type: f.type });
      addFile(named, 'paste');
    }
    if (text.trim()) insertTextAtCaret(text);
    return;
  }
  if (/<img/i.test(html)) {
    if (tryHandleHtmlImage(html)) {
      e.preventDefault();
      return;
    }
    return; // 兜底走原生粘贴
  }
  if (text && text.length > MAX_PASTE_TEXT) {
    e.preventDefault();
    insertTextAtCaret(text.slice(0, MAX_PASTE_TEXT));
    message.warning(`粘贴文本超 ${MAX_PASTE_TEXT} 字符，已截断`);
  }
}

function onDrop(e: DragEvent): void {
  e.preventDefault();
  dragOver.value = false;
  if (agentStore.isGenerating || askMasked.value) return;
  const files = collectImageFiles(e.dataTransfer);
  if (files.length === 0 && e.dataTransfer && e.dataTransfer.files.length > 0) {
    message.error('仅支持 jpg/png/webp，请转换后发送');
    return;
  }
  for (const f of files) addFile(f, 'drop');
}

function onDragOver(e: DragEvent): void {
  e.preventDefault();
  if (!agentStore.isGenerating && !askMasked.value) dragOver.value = true;
}

function onDragLeave(): void {
  dragOver.value = false;
}

function openPicker(): void {
  if (agentStore.isGenerating || askMasked.value) return;
  fileRef.value?.click();
}

function onPickerChange(): void {
  const input = fileRef.value;
  if (!input || !input.files) return;
  for (let i = 0; i < input.files.length; i++) {
    const f = input.files[i];
    if (f) addFile(f, 'picker');
  }
  input.value = '';
}

// ---------- 编辑事件 ----------
function onEditorInput(): void {
  readEditorToDraft();
}

function onEditorClick(e: MouseEvent): void {
  const t = e.target as HTMLElement | null;
  const x = t && typeof t.closest === 'function' ? t.closest('[data-x]') as HTMLElement | null : null;
  if (x) {
    e.preventDefault();
    e.stopPropagation();
    const attId = x.getAttribute('data-x') || '';
    if (attId) removeImage(attId);
  }
}

/** Backspace 紧贴 pill 时整块删除（一次，不留悬空占位符） */
function onBeforeInput(e: InputEvent): void {
  if (e.inputType !== 'deleteContentBackward') return;
  const sel = window.getSelection();
  if (!sel || !sel.isCollapsed || !editorRef.value) return;
  const anchor = sel.anchorNode;
  let pill: HTMLElement | null = null;
  if (anchor && anchor.nodeType === 3) {
    if (sel.anchorOffset === 0) {
      const prev = anchor.previousSibling as HTMLElement | null;
      if (prev && prev.nodeType === 1 && prev.hasAttribute && prev.hasAttribute('data-att')) pill = prev;
    }
  } else if (anchor && (anchor === editorRef.value as unknown as Node)) {
    const child = editorRef.value.childNodes[sel.anchorOffset - 1] as HTMLElement | undefined;
    if (child && child.nodeType === 1 && child.hasAttribute && child.hasAttribute('data-att')) pill = child;
  }
  if (pill) {
    e.preventDefault();
    const attId = pill.getAttribute('data-att') || '';
    if (attId) removeImage(attId);
  }
}

function onKeydown(e: KeyboardEvent): void {
  if (e.key === 'Enter' && !e.shiftKey && !(e as unknown as { isComposing?: boolean }).isComposing) {
    e.preventDefault();
    void handleSend();
  } else if (e.key === 'Escape' && agentStore.isGenerating) {
    e.preventDefault();
    void handleStop();
  }
}

// ---------- 发送 ----------
async function handleCompactCommand(): Promise<void> {
  const sid = workspaceStore.activeSessionId || workspaceStore.activeSession?.id || '';
  clearDraft();
  if (!sid) return;
  workspaceStore.touchSessionTime(sid);
  await workspaceStore.compactSession(sid);
}

function clearDraft(): void {
  cleanupObjectUrls(draft);
  setDraft(createEmptyDraft());
  renderEditorFromText('');
  const sid = workspaceStore.activeSessionId || '';
  if (sid) delete savedDrafts[sid];
}

function plainTextOfDraft(): string {
  return draft.segments
    .filter((s) => s.kind === 'text')
    .map((s) => (s as { kind: 'text'; text: string }).text)
    .join('');
}

async function handleSend(): Promise<void> {
  if (agentStore.isGenerating || askMasked.value) return;
  if (!canSend.value) return;
  readEditorToDraft();
  if (isEmpty(draft)) return;

  const payload = await toSendPayload(draft);
  // /compact 优先（图片忽略）
  const textOnly = payload.text.replace(/\[image:[^\]]+\]/g, '').trim();
  if (textOnly === '/compact') {
    await handleCompactCommand();
    return;
  }

  let attachments = payload.attachments;
  let text = payload.text;
  const hasInvalid = attachmentList.value.some((a) => a.status !== 'ready');
  // 超限取最新 N 个（与后端网关一致，取新丢旧）
  if (attachments.length > maxImages.value) {
    const kept = attachments.slice(attachments.length - maxImages.value);
    const keptIds = new Set(kept.map((a) => a.attId));
    const droppedIds = attachments.filter((a) => !keptIds.has(a.attId)).map((a) => a.attId);
    for (const id of droppedIds) text = text.split(`[image:${id}]`).join('');
    attachments = kept;
  }

  if (!text.trim() && attachments.length === 0) {
    if (hasInvalid) message.warning('图片不可用（超限或需重新添加），且无文字可发送');
    return;
  }
  if (hasInvalid && attachments.length === 0 && text.trim()) {
    message.warning('不可用的图片已跳过，仅发送文字');
  }

  // 非视觉模型一次确认
  if (attachments.length > 0 && !supportsVision.value) {
    try {
      if (window.localStorage.getItem('halo-skip-novision-warn') !== '1') {
        const confirmed = await new Promise<boolean>((resolve) => {
          dialog.warning({
            title: '当前模型不支持看图',
            content: '发送时将自动忽略图片（文字仍发送），是否继续？',
            positiveText: '仍发送',
            negativeText: '取消',
            onPositiveClick: () => {
              try { window.localStorage.setItem('halo-skip-novision-warn', '1'); } catch { /* ignore */ }
              resolve(true);
              return true;
            },
            onNegativeClick: () => {
              resolve(false);
              return true;
            },
            onClose: () => {
              resolve(false);
              return true;
            },
          });
        });
        if (!confirmed) return;
      }
    } catch { /* 确认框异常则继续发送 */ }
  }

  const ok = await agentStore.sendPrompt(text, attachments);
  if (ok) clearDraft();
}

function handleStop(): void {
  void agentStore.stopGenerating();
}

// ---------- 会话切换：文字保留，图片变 invalid 占位 ----------
function snapshotCurrent(): SavedDraft {
  readEditorToDraft();
  return {
    text: plainTextOfDraft(),
    images: attachmentList.value.map((a) => ({ fileName: a.fileName, mime: a.mime })),
  };
}

function restoreDraft(saved: SavedDraft | undefined): void {
  cleanupObjectUrls(draft);
  const next = createEmptyDraft();
  if (saved && saved.text) next.segments.push({ kind: 'text', text: saved.text });
  if (saved) {
    for (const meta of saved.images) {
      const attId = genAttId();
      let file: File;
      try {
        file = new File([], meta.fileName, { type: meta.mime });
      } catch {
        continue;
      }
      next.attachments[attId] = {
        attId,
        fileName: meta.fileName,
        mime: meta.mime,
        sizeBytes: 0,
        objectUrl: '',
        file,
        source: 'paste',
        status: 'invalid',
        error: '切换会话后需重新添加图片',
      };
      next.segments.push({ kind: 'image', attId });
    }
  }
  setDraft(next);
  // 渲染：文本 + invalid 占位 pill
  const editor = editorRef.value;
  if (editor) {
    editor.innerHTML = '';
    if (saved && saved.text) {
      const span = document.createElement('span');
      span.textContent = saved.text;
      editor.appendChild(span);
    }
    for (const s of next.segments) {
      if (s.kind === 'image') {
        const att = next.attachments[s.attId];
        if (att) editor.appendChild(createPillEl(att));
      }
    }
  }
  textLen.value = saved ? saved.text.length : 0;
}

watch(
  () => workspaceStore.activeSessionId,
  (newSid, oldSid) => {
    if (oldSid) savedDrafts[oldSid] = snapshotCurrent();
    restoreDraft(newSid ? savedDrafts[newSid] : undefined);
  }
);

watch(askMasked, (v) => {
  if (editorRef.value) editorRef.value.contentEditable = v ? 'false' : 'true';
});

function handleGlobalKeydown(e: KeyboardEvent): void {
  if (e.key === 'Escape' && agentStore.isGenerating) {
    void handleStop();
  }
}

function focusInput(): void {
  try {
    editorRef.value?.focus();
  } catch { /* ignore */ }
}

let unsubs: Array<() => void> = [];

onMounted(() => {
  agentStore.bind();
  window.addEventListener('keydown', handleGlobalKeydown);
  restoreDraft(workspaceStore.activeSessionId ? savedDrafts[workspaceStore.activeSessionId] : undefined);
  unsubs.push(
    haloEventBus.on<{ text?: string; sessionId?: string }>('chat:prefill-draft', (payload) => {
      try {
        const sid = String(payload?.sessionId || '');
        if (sid && sid !== (workspaceStore.activeSessionId || '')) return;
        insertTextAtCaret(String(payload?.text || ''));
        focusInput();
      } catch { /* ignore */ }
    })
  );
  // need09-06 联动：问询遮罩（不直接依赖 askUserStore，避免跨轨道耦合）
  unsubs.push(
    haloEventBus.on<{ sessionId?: string }>('agent:ask-user-requested', (payload) => {
      try {
        const sid = String(payload?.sessionId || '');
        if (sid && sid !== (workspaceStore.activeSessionId || '')) return;
        askMasked.value = true;
      } catch { /* ignore */ }
    })
  );
  const unmask = (payload: { sessionId?: string }) => {
    try {
      const sid = String(payload?.sessionId || '');
      if (sid && sid !== (workspaceStore.activeSessionId || '')) return;
      askMasked.value = false;
    } catch { /* ignore */ }
  };
  unsubs.push(haloEventBus.on<{ sessionId?: string }>('agent:ask-user-resolved', unmask));
  unsubs.push(
    haloEventBus.on<{ sessionId?: string }>('agent:turn-event', (payload) => {
      try {
        const p = payload as unknown as { sessionId?: string; kind?: string };
        if (p && (p.kind === 'TurnCompleted' || p.kind === 'TurnCancelled' || p.kind === 'TurnError')) unmask(p);
      } catch { /* ignore */ }
    })
   );
 });
onUnmounted(() => {
  window.removeEventListener('keydown', handleGlobalKeydown);
  for (const u of unsubs) {
    try { u(); } catch { /* ignore */ }
  }
  unsubs = [];
});
</script>

<style>
/* need09-04：contenteditable 编辑区 + pill（动态创建节点无 scoped attr，用全局类；chrome109 安全子集） */
.composer-editor {
  min-height: 52px;
  max-height: 220px;
  overflow-y: auto;
  outline: none;
  font-size: 12px;
  line-height: 1.6;
  color: var(--text-primary, #e5e7eb);
  word-break: break-word;
  white-space: pre-wrap;
}
.composer-editor:empty::before {
  content: '输入任务指令，Enter 发送，Shift+Enter 换行，可粘贴/拖入图片…';
  color: var(--text-secondary, #6b7280);
  pointer-events: none;
}
.composer-editor.masked {
  opacity: 0.6;
  pointer-events: none;
}
.composer-box.drag-over {
  border-color: rgba(59, 130, 246, 0.9);
  box-shadow: 0 0 0 2px rgba(59, 130, 246, 0.25);
}
.img-tag {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  border: 1px solid var(--border-subtle, #374151);
  border-radius: 6px;
  padding: 2px 6px;
  font-size: 12px;
  background: var(--bg-elevated, #1f2937);
  vertical-align: middle;
  margin: 0 2px;
  user-select: none;
}
.img-tag.invalid {
  border-color: #f43f5e;
  background: rgba(244, 63, 94, 0.08);
}
.img-tag .img-thumb {
  width: 32px;
  height: 32px;
  object-fit: cover;
  border-radius: 4px;
}
.img-tag .fname {
  max-width: 120px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.img-tag .img-x {
  border: none;
  background: transparent;
  cursor: pointer;
  font-size: 14px;
  line-height: 1;
  padding: 0 2px;
  color: inherit;
}
.img-tag .img-x:hover {
  color: #f43f5e;
}
</style>
