/** need09-04：输入框段模型唯一事实源（纯函数，可单测；禁止调 bridge/DOM）。 */
import { skillMarker, splitBySkillMarkers } from './skillMarker';

export type ComposerSegment =
  | { kind: 'text'; text: string }
  | { kind: 'image'; attId: string }
  /** need22-F02：技能段（scope 与 SlashScope 同形；此处直接写字面量联合，避免耦合） */
  | { kind: 'skill'; scope: 'global' | 'project'; name: string };

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
  /** S7-E1：raw 字节数（清单转 base64 前就已知，供三段式入口校验用，避免先转码再判超限） */
  sizeBytes: number;
}

export interface SendPayload {
  text: string;
  attachments: SendAttachment[];
}

// S4 单图 raw 上限（十进制，对齐 provider 单图 10MB；后端 ImageAttachmentStore.MaxImageBytes 须同值，跨语言硬编码镜像）。
export const MAX_IMAGE_BYTES = 10_000_000;
// S7-E1 入口三段式预算（个数 → 单体 → 总量，与参考 MAX_FILES=20 / 总量 128MB 同量级）：
// 个数取 20（前端允许准备 20 张，实际发包仍受模型 MaxImagesPerTurn / 后端字节盘二次约束）；
// 总量取十进制 128MB raw（base64 前口径，避免超长 payload 过 Bridge 字符串）。
export const MAX_FILES_PER_TURN = 20;
export const MAX_TOTAL_BYTES = 128_000_000;
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
  // 悬空引用自愈：仍被引用的非法 attId 不留段（need22-F02：skill 段恒保留）
  next.segments = next.segments.filter((s) => s.kind === 'text' || s.kind === 'skill' || next.attachments[s.attId]);
  return next;
}

/**
 * need22-F02（D4）：同一 (scope,name) 在一条消息里只保留一个 marker。
 * 保留首次出现的位置，后续同名 skill 段丢弃。
 */
function dedupeSkillSegments(segments: ComposerSegment[]): ComposerSegment[] {
  const seen = new Set<string>();
  const out: ComposerSegment[] = [];
  for (const s of segments) {
    if (s.kind === 'skill') {
      const key = `${s.scope}/${s.name}`;
      if (seen.has(key)) continue;
      seen.add(key);
    }
    out.push(s);
  }
  return out;
}

/** text 段原文 + image 段→`[image:<attId>]` + skill 段→marker；同名 skill 只出一个（D4） */
export function serialize(draft: ComposerDraft): string {
  let out = '';
  for (const s of dedupeSkillSegments(draft.segments)) {
    if (s.kind === 'text') out += s.text;
    else if (s.kind === 'image') {
      if (draft.attachments[s.attId]) out += `[image:${s.attId}]`;
    } else out += skillMarker(s.scope, s.name);
  }
  return out;
}

/** 反序列化：先按 marker 切分，再在文本段内按 `[image:<attId>]` 占位符切分（附件缺失则占位符原文保留为文本） */
export function deserialize(text: string, attachments: Record<string, ComposerAttachment>): ComposerDraft {
  const next = createEmptyDraft();
  next.attachments = { ...attachments };
  const src = String(text || '');
  for (const part of splitBySkillMarkers(src)) {
    if (part.type === 'skill') {
      next.segments.push({ kind: 'skill', scope: part.scope, name: part.name });
      continue;
    }
    const seg = part.text;
    let last = 0;
    IMAGE_PLACEHOLDER_RE.lastIndex = 0;
    let m: RegExpExecArray | null;
    while ((m = IMAGE_PLACEHOLDER_RE.exec(seg)) !== null) {
      const attId = m[1];
      if (m.index > last) next.segments.push({ kind: 'text', text: seg.slice(last, m.index) });
      if (next.attachments[attId]) next.segments.push({ kind: 'image', attId });
      else next.segments.push({ kind: 'text', text: m[0] });
      last = m.index + m[0].length;
    }
    if (last < seg.length) next.segments.push({ kind: 'text', text: seg.slice(last) });
  }
  // D4：还原出的重复 skill 段同样去重
  next.segments = dedupeSkillSegments(next.segments);
  return next;
}

/** need22-F02：移除某个 skill 段（按 scope+name 移除全部同名段） */
export function removeSkillSegment(draft: ComposerDraft, scope: 'global' | 'project', name: string): ComposerDraft {
  const next = cloneDraft(draft);
  next.segments = next.segments.filter((s) => !(s.kind === 'skill' && s.scope === scope && s.name === name));
  return next;
}

/** need22-F02：草稿里是否已含该 skill */
export function hasSkillSegment(draft: ComposerDraft, scope: 'global' | 'project', name: string): boolean {
  return draft.segments.some((s) => s.kind === 'skill' && s.scope === scope && s.name === name);
}

/** I3：去掉占位符后的文本 trim==='' 且无 ready 附件（need22-F02：存在 skill 段 ⇒ 非空） */
export function isEmpty(draft: ComposerDraft): boolean {
  if (draft.segments.some((s) => s.kind === 'skill')) return false;
  const text = draft.segments
    .filter((s) => s.kind === 'text')
    .map((s) => (s as { kind: 'text'; text: string }).text)
    .join('')
    .trim();
  if (text !== '') return false;
  return !Object.values(draft.attachments).some(
    (a) => a.status === 'ready' && draft.segments.some((s) => s.kind === 'image' && s.attId === a.attId)
  );
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

/** S7-E1：入口逐项丢弃原因（前端提示逐项展示，不用一句笼统「超限」）。 */
export type DropReason = 'over-count' | 'over-single' | 'over-total';

export interface AttachmentDrop {
  attId: string;
  fileName: string;
  reason: DropReason;
}

export interface ValidatedAttachments {
  kept: SendAttachment[];
  dropped: AttachmentDrop[];
  /** 被占位符清理后的文本（dropped 的 attId 不留悬空 `[image:attId]`） */
  text: string;
}

export const DROP_REASON_TEXT: Record<DropReason, string> = {
  'over-count': `超过单轮张数上限 ${MAX_FILES_PER_TURN} 张`,
  'over-single': '单张超过 10MB',
  'over-total': '本轮图片总量超过 128MB',
};

/**
 * S7-E1 §4.1：三段式入口校验（个数 → 单体 → 总量），纯函数、可单测。
 *
 * 语义（与文档一致，顺序冻结）：
 *   ① 个数：超过 MAX_FILES_PER_TURN 时保**最新**（数组尾部）N 张，早的逐条丢；
 *   ② 单体：单张 > MAX_IMAGE_BYTES 逐条丢（理论上入口已拦，这里防 Bridge 直调/老草稿）；
 *   ③ 总量：累计 > MAX_TOTAL_BYTES 时从**最旧**开始继续丢，直到不超（保留最新，与①②同向）。
 *
 * 占位符清理：对每个 dropped 的 attId，把文本中**所有**出现处删掉（同一 attId 出现两次也一并清理），
 * 保证不产生悬空 `[image:attId]`（否则后端会当成未知占位符）。
 *
 * 注意：这个函数只做「能不能发」的判定；返回的 attachments 已按 kept 过滤，text 已同步清理。
 */
export function validateAttachments(
  text: string,
  attachments: SendAttachment[],
  limits: { maxFiles?: number; maxSingleBytes?: number; maxTotalBytes?: number } = {}
): ValidatedAttachments {
  const maxFiles = limits.maxFiles ?? MAX_FILES_PER_TURN;
  const maxSingle = limits.maxSingleBytes ?? MAX_IMAGE_BYTES;
  const maxTotal = limits.maxTotalBytes ?? MAX_TOTAL_BYTES;
  const src = String(text ?? '');
  const list = Array.isArray(attachments) ? attachments : [];
  if (list.length === 0) return { kept: [], dropped: [], text: src };

  const drops = new Map<string, DropReason>();
  const drop = (a: SendAttachment, reason: DropReason): void => {
    // 先命中的原因优先（三段式顺序：个数 > 单体 > 总量）
    if (!drops.has(a.attId)) drops.set(a.attId, reason);
  };

  // ① 个数：保最新（尾部）N 张
  let candidates = list.slice();
  if (maxFiles > 0 && candidates.length > maxFiles) {
    const cut = candidates.length - maxFiles;
    for (const a of candidates.slice(0, cut)) drop(a, 'over-count');
    candidates = candidates.slice(cut);
  }

  // ② 单体：逐张
  candidates = candidates.filter((a) => {
    const size = Number.isFinite(a.sizeBytes) ? a.sizeBytes : 0;
    if (maxSingle > 0 && size > maxSingle) {
      drop(a, 'over-single');
      return false;
    }
    return true;
  });

  // ③ 总量：从最旧开始继续丢（保最新）
  if (maxTotal > 0) {
    let total = candidates.reduce((s, a) => s + (Number.isFinite(a.sizeBytes) ? a.sizeBytes : 0), 0);
    let i = 0;
    while (total > maxTotal && i < candidates.length) {
      total -= Number.isFinite(candidates[i].sizeBytes) ? candidates[i].sizeBytes : 0;
      drop(candidates[i], 'over-total');
      i++;
    }
    if (i > 0) candidates = candidates.slice(i);
  }

  let cleaned = src;
  for (const attId of drops.keys()) {
    if (!attId) continue;
    cleaned = cleaned.split(`[image:${attId}]`).join('');
  }

  return {
    kept: candidates,
    dropped: list
      .filter((a) => drops.has(a.attId))
      .map((a) => ({ attId: a.attId, fileName: a.fileName, reason: drops.get(a.attId) as DropReason })),
    text: cleaned,
  };
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

/** I2：text 占位符与 attachments 数组 1:1（仅 ready 被引用附件转 base64）；need22-F02：skill 段拼 marker（不做 ready 过滤） */
export async function toSendPayload(draft: ComposerDraft): Promise<SendPayload> {
  const ready = readyAttachments(draft);
  const readyIds = new Set(ready.map((a) => a.attId));
  let text = '';
  for (const s of dedupeSkillSegments(draft.segments)) {
    if (s.kind === 'text') text += s.text;
    else if (s.kind === 'image') {
      if (readyIds.has(s.attId)) text += `[image:${s.attId}]`;
    } else text += skillMarker(s.scope, s.name);
  }
  const attachments: SendAttachment[] = [];
  for (const a of ready) {
    attachments.push({
      attId: a.attId,
      fileName: a.fileName,
      mime: a.mime,
      base64: await fileToBase64(a.file),
      // S7-E1：raw 字节数随清单下发，供三段式入口校验（避免先转码再判超限）
      sizeBytes: a.sizeBytes,
    });
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
      try {
        URL.revokeObjectURL(oldest);
      } catch (e) {
        console.warn('[composer] lru revoke failed:', e);
      }
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
  try {
    URL.revokeObjectURL(url);
  } catch (e) {
    console.warn('[composer] revoke failed:', e);
  }
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
    if (!ctx) {
      bitmap.close();
      return file;
    }
    ctx.drawImage(bitmap, 0, 0, w, h);
    bitmap.close();
    const blob = await new Promise<Blob | null>((resolve) => {
      try {
        canvas.toBlob((b) => resolve(b), 'image/jpeg', 0.82);
      } catch (e) {
        console.warn('[composer] toBlob failed:', e);
        resolve(null);
      }
    });
    if (!blob) return file;
    return new File([blob], file.name, { type: 'image/jpeg' });
  } catch (e) {
    console.warn('[composer] compress failed, fallback original:', e);
    return file;
  }
}
