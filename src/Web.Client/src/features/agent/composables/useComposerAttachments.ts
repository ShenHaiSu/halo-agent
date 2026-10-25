import type { ComputedRef, Ref } from 'vue';
import {
  createPreviewUrl,
  compressImageIfNeeded,
  genAttId,
  insertImage,
  MAX_IMAGE_BYTES,
  MAX_PASTE_TEXT,
  removeAttachment,
  revokePreviewUrl,
  safeFileName,
  type AttachmentSource,
  type ComposerAttachment,
  type ComposerDraft,
  type ComposerImageMime,
} from '@shared/utils/composerModel';
import { acceptImageFile, collectImageFiles, extractPaste, UNSUPPORTED_IMAGE_REASON } from '@shared/utils/clipboardImage';
import { formatSmartBytesShort } from '@shared/utils/formatSmartBytes';
import { pasteHtmlImage } from './composerEditorDom';

export interface AttachmentDeps {
  editorRef: Ref<HTMLElement | null>;
  fileRef: Ref<HTMLInputElement | null>;
  isGenerating: ComputedRef<boolean>;
  askMasked: Ref<boolean>;
  getDraft(): ComposerDraft;
  setDraft(next: ComposerDraft): void;
  readEditorToDraft(): void;
  insertPillAtCaret(att: ComposerAttachment): void;
  insertTextAtCaret(text: string): void;
  warn(msg: string): void;
  error(msg: string): void;
  setDragOver(v: boolean): void;
}
let lastPasteSig = '';
let lastPasteAt = 0;

export function useComposerAttachments(deps: AttachmentDeps) {
  function probeSize(attId: string, url: string): void {
    try {
      const probe = new Image();
      probe.onload = () => {
        const hit = deps.getDraft().attachments[attId];
        if (hit) {
          hit.width = probe.naturalWidth;
          hit.height = probe.naturalHeight;
        }
      };
      probe.onerror = () => undefined;
      probe.src = url;
    } catch (e) {
      console.warn('[composer] probe failed:', e);
    }
  }
  function addFile(f: File, source: AttachmentSource): void {
    const chk = acceptImageFile(f);
    if (!chk.ok) {
      deps.error(chk.reason || UNSUPPORTED_IMAGE_REASON);
      return;
    }
    const mime = chk.mime as ComposerImageMime;
    const attId = genAttId();
    const oversize = f.size > MAX_IMAGE_BYTES;
    const att: ComposerAttachment = {
      attId,
      fileName: safeFileName(f.name || `pasted-${Date.now()}.png`),
      mime,
      sizeBytes: f.size,
      objectUrl: createPreviewUrl(f),
      file: f,
      source,
      status: oversize ? 'oversize' : 'ready',
      error: oversize ? `单张超过 10MB（${formatSmartBytesShort(f.size)}），发送时自动跳过` : undefined,
    };
    const draft = deps.getDraft();
    draft.attachments[attId] = att;
    deps.setDraft(insertImage(draft, attId));
    deps.insertPillAtCaret(att);
    if (oversize) deps.warn('图片超过 10MB，发送时将自动跳过（文字仍发送）');
    void compressImageIfNeeded(f).then((smaller) => {
      try {
        if (smaller === f) return;
        const hit = deps.getDraft().attachments[attId];
        if (!hit) return;
        revokePreviewUrl(hit.objectUrl);
        hit.objectUrl = createPreviewUrl(smaller);
        probeSize(attId, hit.objectUrl);
      } catch (e) {
        console.warn('[composer] compressed preview failed:', e);
      }
    });
    probeSize(attId, att.objectUrl);
  }
  function removeImage(attId: string): void {
    const draft = deps.getDraft();
    const hit = draft.attachments[attId];
    try {
      if (hit?.objectUrl) revokePreviewUrl(hit.objectUrl);
    } catch (e) {
      console.warn('[composer] revoke failed:', e);
    }
    deps.setDraft(removeAttachment(draft, attId));
    try {
      const pill = deps.editorRef.value?.querySelector(`[data-att="${attId}"]`);
      pill?.parentNode?.removeChild(pill);
    } catch {
      /* ignore */
    }
    deps.readEditorToDraft();
    deps.editorRef.value?.focus();
  }
  function onPaste(e: ClipboardEvent): void {
    if (deps.isGenerating.value || deps.askMasked.value) {
      e.preventDefault();
      return;
    }
    const { files, text, html } = extractPaste(e);
    if (files.length > 0) {
      e.preventDefault();
      e.stopPropagation();
      const sig = files.map((f) => `${f.size}|${String(f.type || '').toLowerCase()}`).join(';') + `|t${text.length}|h${html.length}`;
      const now = Date.now();
      if (sig === lastPasteSig && now - lastPasteAt < 300) return;
      lastPasteSig = sig;
      lastPasteAt = now;
      for (const f of files) addFile(f.name ? f : new File([f], `pasted-${Date.now()}.png`, { type: f.type }), 'paste');
      if (text.trim()) deps.insertTextAtCaret(text);
      return;
    }
    if (/<img/i.test(html)) {
      if (pasteHtmlImage(html, addFile, deps.insertTextAtCaret)) {
        e.preventDefault();
        e.stopPropagation();
      }
      return;
    }
    if (text && text.length > MAX_PASTE_TEXT) {
      e.preventDefault();
      deps.insertTextAtCaret(text.slice(0, MAX_PASTE_TEXT));
      deps.warn(`粘贴文本超 ${MAX_PASTE_TEXT} 字符，已截断`);
    }
  }
  function onDrop(e: DragEvent): void {
    e.preventDefault();
    deps.setDragOver(false);
    if (deps.isGenerating.value || deps.askMasked.value) return;
    const files = collectImageFiles(e.dataTransfer);
    if (files.length === 0 && e.dataTransfer && e.dataTransfer.files.length > 0) {
      deps.error(UNSUPPORTED_IMAGE_REASON);
      return;
    }
    for (const f of files) addFile(f, 'drop');
  }
  function onPickerChange(): void {
    const input = deps.fileRef.value;
    if (!input?.files) return;
    for (let i = 0; i < input.files.length; i++) {
      const f = input.files[i];
      if (f) addFile(f, 'picker');
    }
    input.value = '';
  }
  return { addFile, removeImage, onPaste, onDrop, onPickerChange };
}
export type ComposerAttachments = ReturnType<typeof useComposerAttachments>;
