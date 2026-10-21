/** need09-04：paste/drop 解析纯函数（禁止碰 DOM/Store，可单测）。 */
import type { ComposerImageMime } from '@/utils/composerModel';

export const ACCEPT_IMAGE_MIMES: ComposerImageMime[] = ['image/jpeg', 'image/png', 'image/webp'];

export interface AcceptResult {
  ok: boolean;
  mime?: ComposerImageMime;
  reason?: string;
}

function normalizeMime(raw: string, fileName: string): ComposerImageMime | null {
  const m = String(raw || '').toLowerCase();
  if (m === 'image/jpeg' || m === 'image/jpg') return 'image/jpeg';
  if (m === 'image/png') return 'image/png';
  if (m === 'image/webp') return 'image/webp';
  // mime 缺失（如某些拖拽场景）时按扩展名兜底
  const ext = String(fileName || '').toLowerCase().split('.').pop() || '';
  if (ext === 'jpg' || ext === 'jpeg') return 'image/jpeg';
  if (ext === 'png') return 'image/png';
  if (ext === 'webp') return 'image/webp';
  return null;
}

/** 白名单校验：仅 jpg/png/webp */
export function acceptImageFile(f: File): AcceptResult {
  const mime = normalizeMime(f.type, f.name);
  if (!mime) return { ok: false, reason: '仅支持 jpg/png/webp，请转换后发送' };
  return { ok: true, mime };
}

type ClipboardLike = {
  items?: DataTransferItemList | null;
  files?: FileList | File[] | null;
  getData?: (format: string) => string;
};

/** items[type^='image/'] + files[type^='image/'] 去重合并。
  * 成因 fix（重复录入）: 同一张剪贴板图片在 Chrome/CefSharp 下会同时出现在
  * `items.getAsFile()` 与 `files[0]` 两个位置，且两者 `lastModified` 常不一致
  * （截图流甚至 `name` 为空），旧 key `name|size|lastModified` 去重失效，
  * 单次 Ctrl+V 被录入两次。改为分阶段：先收 items，再收 files 时若已有
  * 同 size+type 的 items 项则视为同一张跨源重复，直接跳过（name 忽略）。
  * 同源多图（如资源管理器多选36）仍按 name+size+type 区分，互不吞并。 */
 export function collectImageFiles(cd: ClipboardLike | DataTransfer | null): File[] {
   if (!cd) return [];
   const fromItems: File[] = [];
   const out: File[] = [];
   const seenSameSource = new Set<string>();
   const sameSourceKey = (f: File) => `${f.name || ''}|${f.size}|${String(f.type || '').toLowerCase()}`;
   const pushSameSource = (list: File[], f: File | null | undefined): void => {
     if (!f || typeof (f as File).type !== 'string') return;
     if (!String((f as File).type).toLowerCase().startsWith('image/')) return;
     const key = sameSourceKey(f as File);
     if (seenSameSource.has(key)) return;
     seenSameSource.add(key);
     list.push(f as File);
   };
   try {
     const items = (cd as ClipboardLike).items;
     if (items) {
       for (let i = 0; i < items.length; i++) {
         const it = items[i];
         if (it && it.kind === 'file' && String(it.type || '').toLowerCase().startsWith('image/')) {
           pushSameSource(fromItems, it.getAsFile());
         }
       }
     }
   } catch {
     /* ignore */
   }
   for (const f of fromItems) out.push(f);
   // files 源：跨源去重只比 size+type（name/lastModified 在双源复用时不可靠）
   const hasSameImage = (f: File): boolean =>
     fromItems.some((a) => a.size === f.size && String(a.type || '').toLowerCase() === String(f.type || '').toLowerCase());
   try {
     const files = (cd as ClipboardLike).files;
     if (files) {
       const len = (files as FileList).length !== undefined ? (files as FileList).length : (files as File[]).length;
       for (let i = 0; i < len; i++) {
         const f = (files as FileList)[i] as File;
         if (!f || typeof f.type !== 'string') continue;
         if (!String(f.type).toLowerCase().startsWith('image/')) continue;
         if (hasSameImage(f)) continue;
         const key = sameSourceKey(f);
         if (seenSameSource.has(key)) continue;
         seenSameSource.add(key);
         out.push(f);
       }
     }
   } catch {
     /* ignore */
   }
   return out;
 }

export interface PasteExtract {
  files: File[];
  text: string;
  html: string;
}

/** paste 手势事件解析（不碰 DOM；CefSharp 下禁用 navigator.clipboard.read） */
export function extractPaste(e: ClipboardEvent): PasteExtract {
  const cd = e.clipboardData || null;
  let text = '';
  let html = '';
  try {
    text = cd && typeof cd.getData === 'function' ? cd.getData('text/plain') || '' : '';
  } catch {
    text = '';
  }
  try {
    html = cd && typeof cd.getData === 'function' ? cd.getData('text/html') || '' : '';
  } catch {
    html = '';
  }
  return { files: collectImageFiles(cd), text, html };
}

/** 从 text/html 中提取首个 <img src>（网页复制图片分支用） */
export function firstHtmlImgSrc(html: string): string {
  const m = /<img[^>]+src\s*=\s*["']([^"']+)["']/i.exec(String(html || ''));
  return m ? m[1] : '';
}
