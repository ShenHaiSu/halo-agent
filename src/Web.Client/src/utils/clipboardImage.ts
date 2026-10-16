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

/** items[type^='image/'] + files[type^='image/'] 去重合并 */
export function collectImageFiles(cd: ClipboardLike | DataTransfer | null): File[] {
  if (!cd) return [];
  const out: File[] = [];
  const seen = new Set<string>();
  const push = (f: File | null | undefined) => {
    if (!f || typeof (f as File).type !== 'string') return;
    if (!String((f as File).type).toLowerCase().startsWith('image/')) return;
    const key = `${(f as File).name}|${(f as File).size}|${(f as File).lastModified}`;
    if (seen.has(key)) return;
    seen.add(key);
    out.push(f as File);
  };
  try {
    const items = (cd as ClipboardLike).items;
    if (items) {
      for (let i = 0; i < items.length; i++) {
        const it = items[i];
        if (it && it.kind === 'file' && String(it.type || '').toLowerCase().startsWith('image/')) {
          const f = it.getAsFile();
          push(f);
        }
      }
    }
  } catch {
    /* ignore */
  }
  try {
    const files = (cd as ClipboardLike).files;
    if (files) {
      const len = (files as FileList).length !== undefined ? (files as FileList).length : (files as File[]).length;
      for (let i = 0; i < len; i++) push((files as FileList)[i]);
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
