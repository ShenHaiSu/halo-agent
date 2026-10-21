import type { ComposerAttachment, ComposerDraft, ComposerSegment } from '@shared/utils/composerModel';

export function escapeHtml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
export function shortNameOf(n: string): string {
  const arr = [...String(n || '')];
  return arr.length <= 12 ? n : arr.slice(0, 12).join('') + '…';
}
export function createPillEl(att: ComposerAttachment): HTMLSpanElement {
  const span = document.createElement('span');
  span.className = 'img-tag' + (att.status === 'ready' ? '' : ' invalid');
  span.contentEditable = 'false';
  span.setAttribute('data-att', att.attId);
  span.title = att.error || att.fileName;
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
export function readSegments(editor: HTMLElement, hasAtt: (id: string) => boolean): ComposerSegment[] {
  const segments: ComposerSegment[] = [];
  let buf = '';
  const flush = () => {
    if (!buf) return;
    const last = segments.length > 0 ? segments[segments.length - 1] : null;
    if (last && last.kind === 'text') last.text += buf;
    else segments.push({ kind: 'text', text: buf });
    buf = '';
  };
  const walk = (node: Node): void => {
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
    if (attId && hasAtt(attId)) {
      flush();
      segments.push({ kind: 'image', attId });
      return;
    }
    if (el.nodeName === 'DIV' || el.nodeName === 'P') {
      for (let i = 0; i < el.childNodes.length; i++) walk(el.childNodes[i]);
      buf += '\n';
      return;
    }
    for (let i = 0; i < el.childNodes.length; i++) walk(el.childNodes[i]);
  };
  for (let i = 0; i < editor.childNodes.length; i++) walk(editor.childNodes[i]);
  flush();
  const last = segments.length > 0 ? segments[segments.length - 1] : null;
  if (last && last.kind === 'text') last.text = last.text.replace(/\n+$/, '');
  return segments.filter((s) => s.kind === 'text' || hasAtt((s as { attId: string }).attId));
}
export function textLenOf(segments: ComposerSegment[]): number {
  return segments
    .filter((s) => s.kind === 'text')
    .map((s) => (s as { kind: 'text'; text: string }).text)
    .join('').length;
}
export function renderText(editor: HTMLElement, text: string): void {
  editor.innerHTML = text ? escapeHtml(text).replace(/\n/g, '<br>') : '';
}
export function renderDraft(editor: HTMLElement, text: string, atts: ComposerAttachment[]): void {
  editor.innerHTML = '';
  if (text) {
    const span = document.createElement('span');
    span.textContent = text;
    editor.appendChild(span);
  }
  for (const att of atts) editor.appendChild(createPillEl(att));
}
export function placeCaretAfter(editor: HTMLElement, node: Node): void {
  try {
    const sel = window.getSelection();
    if (!sel) return;
    const range = document.createRange();
    range.setStartAfter(node);
    range.collapse(true);
    sel.removeAllRanges();
    sel.addRange(range);
    editor.focus();
  } catch {
    /* ignore */
  }
}
export function insertTextFallback(editor: HTMLElement, text: string): boolean {
  try {
    if (document.execCommand('insertText', false, text)) return true;
  } catch {
    /* ignore */
  }
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
    return true;
  } catch {
    return false;
  }
}
export function insertPill(editor: HTMLElement, att: ComposerAttachment): void {
  const pill = createPillEl(att);
  try {
    const sel = window.getSelection();
    const range = sel && sel.rangeCount > 0 ? sel.getRangeAt(0) : null;
    if (range && editor.contains(range.commonAncestorContainer)) {
      range.deleteContents();
      range.insertNode(pill);
      if (!pill.nextSibling) editor.appendChild(document.createTextNode(''));
      placeCaretAfter(editor, pill);
    } else {
      editor.appendChild(pill);
      editor.appendChild(document.createTextNode(''));
      placeCaretAfter(editor, pill);
    }
  } catch {
    editor.appendChild(pill);
  }
}
export function closestRemoveId(target: EventTarget | null): string {
  const t = target as HTMLElement | null;
  const x = t && typeof t.closest === 'function' ? (t.closest('[data-x]') as HTMLElement | null) : null;
  return x?.getAttribute('data-x') || '';
}
export function backspacePill(editor: HTMLElement): HTMLElement | null {
  const sel = window.getSelection();
  if (!sel || !sel.isCollapsed) return null;
  const anchor = sel.anchorNode;
  if (anchor && anchor.nodeType === 3) {
    if (sel.anchorOffset === 0) {
      const prev = anchor.previousSibling as HTMLElement | null;
      if (prev && prev.nodeType === 1 && prev.hasAttribute && prev.hasAttribute('data-att')) return prev;
    }
    return null;
  }
  if (anchor && anchor === (editor as unknown as Node)) {
    const child = editor.childNodes[sel.anchorOffset - 1] as HTMLElement | undefined;
    if (child && child.nodeType === 1 && child.hasAttribute && child.hasAttribute('data-att')) return child;
  }
  return null;
}
export type { ComposerDraft };
import { firstHtmlImgSrc as _firstImg } from '@shared/utils/clipboardImage';
import type { ComposerImageMime as _Mime } from '@shared/utils/composerModel';
export function pasteHtmlImage(html: string, addFile: (f: File, s: 'paste') => void, insertText: (t: string) => void): boolean {
  const src = _firstImg(html);
  if (!src) return false;
  if (!src.startsWith('data:image/')) {
    try {
      const name = decodeURIComponent(String(src).split('?')[0].split('#')[0].split('/').filter(Boolean).pop() || '网页图片');
      insertText(`[${name} 请拖入图片文件发送]`);
    } catch {
      insertText('[网页图片 请拖入图片文件发送]');
    }
    return true;
  }
  const comma = src.indexOf(',');
  const head = src.slice(0, comma);
  const body = comma >= 0 ? src.slice(comma + 1) : '';
  const raw = head.split(';')[0].slice(5).toLowerCase();
  const mime: _Mime | null =
    raw === 'image/png'
      ? 'image/png'
      : raw === 'image/webp'
        ? 'image/webp'
        : raw === 'image/jpeg' || raw === 'image/jpg'
          ? 'image/jpeg'
          : null;
  if (!mime || !body) return false;
  try {
    const bin = atob(body);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    const ext = mime === 'image/png' ? 'png' : mime === 'image/webp' ? 'webp' : 'jpg';
    addFile(new File([bytes], `pasted-${Date.now()}.${ext}`, { type: mime }), 'paste');
    return true;
  } catch {
    return false;
  }
}
