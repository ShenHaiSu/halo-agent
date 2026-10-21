/** need09-04：输入框段模型唯一事实源（纯函数，可单测；禁止调 bridge/DOM）。 */
export type ComposerSegment =
  | { kind: 'text'; text: string }
  | { kind: 'image'; attId: string };

export type AttachmentSource = 'paste' | 'drop' | 'picker';
export type AttachmentStatus = 'ready' | 'oversize' | 'invalid';

export type ComposerImageMime = 'image/jpeg' | 'image/png' | 'image/webp';

export interface ComposerAttachment {
  attId: string;
  fileName: string;
  mime: ComposerImageMime;
  sizeBytes: number;
  width?: number;
  height?: number;
  /** 仅内存预览，永不发送 */
  objectUrl: string;
  file: File;
  source: AttachmentSource;
  status: AttachmentStatus;
  error?: string;
}

export interface ComposerDraft {
  segments: ComposerSegment[];
  attachments: Record<string, ComposerAttachment>;
}

export interface SendAttachment {
  attId: string;
  fileName: string;
  mime: string;
  base64: string;
}

export interface SendPayload {
  text: string;
  attachments: SendAttachment[];
}

export const MAX_IMAGE_BYTES = 8 * 1024 * 1024;
export const MAX_PASTE_TEXT = 20000;
const IMAGE_PLACEHOLDER_RE = /\[image:([A-Za-z0-9_-]+)\]/g;

export function genAttId(): string {
  let hex = '';
  for (let i = 0; i < 8; i++) hex += Math.floor(Math.random() * 16).toString(16);
  return `att_${hex}`;
}

export function createEmptyDraft(): ComposerDraft {
  return { segments: [], attachments: {} };
}

/** 去 `../\:*?"<>|` 控制字符 + 超 64 字截断 + 空名兜底 */
export function safeFileName(raw: string, fallbackExt = 'png'): string {
  const cleaned = String(raw || '')
    .replace(/[\\/:*?"<>|]/g, '_')
    .replace(/[.]{2,}/g, '_')
    .replace(/[\u0000-\u001f\u007f]/g, '')
    .trim()
    .replace(/^[.\s]+/, '');
  if (!cleaned) return `pasted-${Date.now()}.${fallbackExt}`;
  if (cleaned.length <= 64) return cleaned;
  const dot = cleaned.lastIndexOf('.');
  if (dot > 0 && cleaned.length - dot <= 10) {
    const ext = cleaned.slice(dot);
    return cleaned.slice(0, 64 - ext.length) + ext;
  }
  return cleaned.slice(0, 64);
}

function cloneDraft(d: ComposerDraft): ComposerDraft {
  return {
    segments: d.segments.map((s) => ({ ...s })),
    attachments: { ...d.attachments },
  };
}

/** 文本追加：与末尾 text 段合并 */
export function insertText(draft: ComposerDraft, text: string): ComposerDraft {
  const next = cloneDraft(draft);
  if (!text) return next;
  const last = next.segments.length > 0 ? next.segments[next.segments.length - 1] : null;
  if (last && last.kind === 'text') last.text += text;
  else next.segments.push({ kind: 'text', text });
  return next;
}

/** 图片段插入到末尾（调用方负责光标语义：简化为追加；编辑器按 DOM 顺序重排） */
export function insertImage(draft: ComposerDraft, attId: string): ComposerDraft {
  const next = cloneDraft(draft);
  next.segments.push({ kind: 'image', attId });
  return next;
}

/** 删除附件：同步删 image 段；纯函数（不 revoke，revoke 走 cleanupObjectUrls） */
export function removeAttachment(draft: ComposerDraft, attId: string): ComposerDraft {
  const next: ComposerDraft = {
    segments: draft.segments.filter((s) => !(s.kind === 'image' && s.attId === attId)),
    attachments: { ...draft.attachments },
  };
  delete next.attachments[attId];
  // 悬空引用自愈：仍被引用的非法 attId 不留段
  next.segments = next.segments.filter((s) => s.kind === 'text' || next.attachments[s.attId]);
  return next;
}

/** text 段原文 + image 段→`[image:<attId>]`；仅 ready 且被引用的附件进 attachments（无 base64 的同步版） */
export function serialize(draft: ComposerDraft): string {
  let out = '';
  for (const s of draft.segments) {
    if (s.kind === 'text') out += s.text;
    else if (draft.attachments[s.attId]) out += `[image:${s.attId}]`;
  }
  return out;
}

/** 反序列化：占位符还原为 image 段（附件缺失则占位符原文保留为文本） */
export function deserialize(text: string, attachments: Record<string, ComposerAttachment>): ComposerDraft {
  const next = createEmptyDraft();
  next.attachments = { ...attachments };
  const src = String(text || '');
  let last = 0;
  IMAGE_PLACEHOLDER_RE.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = IMAGE_PLACEHOLDER_RE.exec(src)) !== null) {
    const attId = m[1];
    if (m.index > last) next.segments.push({ kind: 'text', text: src.slice(last, m.index) });
    if (next.attachments[attId]) next.segments.push({ kind: 'image', attId });
    else next.segments.push({ kind: 'text', text: m[0] });
    last = m.index + m[0].length;
  }
  if (last < src.length) next.segments.push({ kind: 'text', text: src.slice(last) });
  return next;
}

/** I3：去掉占位符后的文本 trim==='' 且无 ready 附件 */
export function isEmpty(draft: ComposerDraft): boolean {
  const text = draft.segments
    .filter((s) => s.kind === 'text')
    .map((s) => (s as { kind: 'text'; text: string }).text)
    .join('')
    .trim();
  if (text !== '') return false;
  return !Object.values(draft.attachments).some((a) => a.status === 'ready' && draft.segments.some((s) => s.kind === 'image' && s.attId === a.attId));
}

/** 仅 ready 且被引用的附件（发送候选） */
export function readyAttachments(draft: ComposerDraft): ComposerAttachment[] {
  const out: ComposerAttachment[] = [];
  const seen = new Set<string>();
  for (const s of draft.segments) {
    if (s.kind !== 'image' || seen.has(s.attId)) continue;
    seen.add(s.attId);
    const a = draft.attachments[s.attId];
    if (a && a.status === 'ready') out.push(a);
  }
  return out;
}

function fileToBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    try {
      const reader = new FileReader();
      reader.onload = () => {
        const url = String(reader.result || '');
        const idx = url.indexOf(',');
        resolve(idx >= 0 ? url.slice(idx + 1) : url);
      };
      reader.onerror = () => reject(reader.error || new Error('readAsDataURL failed'));
      reader.readAsDataURL(file);
    } catch (e) {
      reject(e);
    }
  });
}

/** I2：text 占位符与 attachments 数组 1:1（仅 ready 被引用附件转 base64） */
export async function toSendPayload(draft: ComposerDraft): Promise<SendPayload> {
  const ready = readyAttachments(draft);
  const readyIds = new Set(ready.map((a) => a.attId));
  let text = '';
  for (const s of draft.segments) {
    if (s.kind === 'text') text += s.text;
    else if (readyIds.has(s.attId)) text += `[image:${s.attId}]`;
  }
  const attachments: SendAttachment[] = [];
  for (const a of ready) {
    attachments.push({ attId: a.attId, fileName: a.fileName, mime: a.mime, base64: await fileToBase64(a.file) });
  }
  return { text, attachments };
}

/** 发送成功/丢弃草稿：revoke 全部 objectUrl（唯一允许碰 DOM/URL 的出口） */
export function cleanupObjectUrls(draft: ComposerDraft): void {
  for (const a of Object.values(draft.attachments)) {
    try {
      if (a.objectUrl) URL.revokeObjectURL(a.objectUrl);
    } catch {
      /* ignore */
    }
  }
}
 /** need22-P3 G3：缩略图 LRU（20 张）+ URL.createObjectURL 预览，发送才转码，revoke 释放。 */
 export const THUMB_LRU_MAX = 20;
 /** need22-P3 G3：>5MB 预览前压缩阈值（canvas.toBlob）。 */
 export const PREVIEW_COMPRESS_BYTES = 5 * 1024 * 1024;
 const thumbLruOrder: string[] = [];
 function thumbLruTouch(url: string): void {
   const i = thumbLruOrder.indexOf(url);
   if (i >= 0) thumbLruOrder.splice(i, 1);
   thumbLruOrder.push(url);
   while (thumbLruOrder.length > THUMB_LRU_MAX) {
     const oldest = thumbLruOrder.shift();
     if (oldest) {
       try { URL.revokeObjectURL(oldest); } catch (e) { console.warn('[composer] lru revoke failed:', e); }
     }
   }
 }
 /** 预览 URL（objectURL，不转 base64；LRU 满 20 自动 revoke 最旧）。 */
 export function createPreviewUrl(file: File): string {
   const url = URL.createObjectURL(file);
   thumbLruTouch(url);
   return url;
 }
 export function revokePreviewUrl(url: string): void {
   if (!url) return;
   const i = thumbLruOrder.indexOf(url);
   if (i >= 0) thumbLruOrder.splice(i, 1);
   try { URL.revokeObjectURL(url); } catch (e) { console.warn('[composer] revoke failed:', e); }
 }
 /** need22-P3 G3：>5MB 图片压缩后再预览（canvas.toBlob，长边 ≤1920，jpeg 0.82）；失败回退原文件。 */
 export async function compressImageIfNeeded(file: File): Promise<File> {
   try {
     if (file.size <= PREVIEW_COMPRESS_BYTES) return file;
     if (typeof createImageBitmap === 'undefined' || typeof document === 'undefined') return file;
     const bitmap = await createImageBitmap(file);
     const scale = Math.min(1, 1920 / Math.max(bitmap.width, bitmap.height));
     const w = Math.max(1, Math.round(bitmap.width * scale));
     const h = Math.max(1, Math.round(bitmap.height * scale));
     const canvas = document.createElement('canvas');
     canvas.width = w;
     canvas.height = h;
     const ctx = canvas.getContext('2d');
     if (!ctx) { bitmap.close(); return file; }
     ctx.drawImage(bitmap, 0, 0, w, h);
     bitmap.close();
     const blob = await new Promise<Blob | null>((resolve) => {
       try { canvas.toBlob((b) => resolve(b), 'image/jpeg', 0.82); }
       catch (e) { console.warn('[composer] toBlob failed:', e); resolve(null); }
     });
     if (!blob) return file;
     return new File([blob], file.name, { type: 'image/jpeg' });
   } catch (e) {
     console.warn('[composer] compress failed, fallback original:', e);
     return file;
   }
 }
