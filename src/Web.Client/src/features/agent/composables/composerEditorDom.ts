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
export function createSkillPillEl(scope: 'global' | 'project', name: string, title?: string): HTMLSpanElement {
  const span = document.createElement('span');
  span.className = 'skill-tag';
  span.contentEditable = 'false';
  span.setAttribute('data-skill', `${scope}/${name}`);
  span.setAttribute('data-testid', `skill-pill-${scope}-${name}`);
  span.setAttribute('aria-label', `技能 ${scope}/${name}`);
  if (title) span.title = title;

  const badge = document.createElement('span');
  badge.className = 'skill-scope';
  badge.setAttribute('data-scope', scope);
  badge.textContent = scope;
  span.appendChild(badge);

  const label = document.createElement('span');
  label.className = 'skill-name';
  label.textContent = name;
  span.appendChild(label);

  const btn = document.createElement('button');
  btn.type = 'button';
  btn.className = 'skill-x';
  btn.textContent = '×';
  btn.setAttribute('data-x', `skill:${scope}/${name}`);
  btn.setAttribute('data-testid', `skill-remove-${scope}-${name}`);
  btn.setAttribute('aria-label', `移除技能 ${scope}/${name}`);
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
    // need22-F02：skill pill 分支必须在 data-att 分支之前（否则 pill 内部文字会被当正文读进来）
    const skillKey = el.getAttribute && el.getAttribute('data-skill');
    if (skillKey) {
      const slash = skillKey.indexOf('/');
      if (slash > 0) {
        flush();
        segments.push({
          kind: 'skill',
          scope: skillKey.slice(0, slash) === 'project' ? 'project' : 'global',
          name: skillKey.slice(slash + 1),
        });
        return;
      }
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
  return segments.filter((s) => {
    if (s.kind === 'text') return true;
    if (s.kind === 'skill') return true;
    return hasAtt((s as { attId: string }).attId);
  });
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
/**
 * need22-F02：按段序渲染草稿（text / image pill / skill pill），保留原始段序。
 * 破坏性变更：签名由 (editor, text, atts) 改为 (editor, segments, attachments)。
 */
export function renderDraft(editor: HTMLElement, segments: ComposerSegment[], attachments: Record<string, ComposerAttachment>): void {
  editor.innerHTML = '';
  for (const s of segments) {
    if (s.kind === 'text') {
      if (!s.text) continue;
      const span = document.createElement('span');
      span.textContent = s.text;
      editor.appendChild(span);
    } else if (s.kind === 'image') {
      const att = attachments[s.attId];
      if (att) editor.appendChild(createPillEl(att));
    } else {
      editor.appendChild(createSkillPillEl(s.scope, s.name));
    }
  }
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
/** need22-F02：pill 插入公共实现（图片 pill 与 skill pill 共用）。 */
export function insertPillAtRange(editor: HTMLElement, pill: HTMLElement, range: Range | null): void {
  try {
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
export function insertPill(editor: HTMLElement, att: ComposerAttachment): void {
  const pill = createPillEl(att);
  try {
    const sel = window.getSelection();
    const range = sel && sel.rangeCount > 0 ? sel.getRangeAt(0) : null;
    insertPillAtRange(editor, pill, range);
  } catch {
    editor.appendChild(pill);
  }
}
/** need22-F03：在当前选区插入 skill pill。 */
export function insertSkillPill(editor: HTMLElement, scope: 'global' | 'project', name: string, title?: string): void {
  const pill = createSkillPillEl(scope, name, title);
  try {
    const sel = window.getSelection();
    const range = sel && sel.rangeCount > 0 ? sel.getRangeAt(0) : null;
    insertPillAtRange(editor, pill, range);
  } catch {
    editor.appendChild(pill);
  }
}
/** need22-F03：用 pill 替换给定选区（斜杠查询串 → pill）。 */
export function replaceRangeWithPill(editor: HTMLElement, range: Range, pill: HTMLElement): void {
  insertPillAtRange(editor, pill, range);
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
      if (prev && prev.nodeType === 1 && prev.hasAttribute) {
        if (prev.hasAttribute('data-att')) return prev;
        // need22-F02：skill pill 同样整颗删除
        if (prev.hasAttribute('data-skill')) return prev;
      }
    }
    return null;
  }
  if (anchor && anchor === (editor as unknown as Node)) {
    const child = editor.childNodes[sel.anchorOffset - 1] as HTMLElement | undefined;
    if (child && child.nodeType === 1 && child.hasAttribute) {
      if (child.hasAttribute('data-att')) return child;
      if (child.hasAttribute('data-skill')) return child;
    }
  }
  return null;
}

/* ------------------------------------------------------------------ *
 * need22-F03：偏移工具（偏移口径统一：只累计 nodeType===3 文本节点，
 * BR / DIV / P 不计长度）——detectSlashTrigger 与 replaceRangeWithPill 同口径。
 * ------------------------------------------------------------------ */

/** 编辑器纯文本（仅文本节点累计，不含 BR/DIV 换行） */
export function editorPlainText(editor: HTMLElement): string {
  try {
    const r = document.createRange();
    r.selectNodeContents(editor);
    return r.toString();
  } catch {
    return '';
  }
}

/** 编辑器内光标相对"文本节点累计长度"的偏移；不在编辑器内返回 null。 */
export function caretOffsetInEditor(editor: HTMLElement): number | null {
  const sel = window.getSelection();
  if (!sel || sel.rangeCount === 0) return null;
  const anchor = sel.anchorNode;
  if (!anchor) return null;
  if (anchor !== editor && !editor.contains(anchor)) return null;
  try {
    const r = document.createRange();
    r.selectNodeContents(editor);
    r.setEnd(anchor, sel.anchorOffset);
    return r.toString().length;
  } catch {
    return null;
  }
}

function locateTextOffset(editor: HTMLElement, target: number): { node: Text; offset: number } | null {
  const walker = document.createTreeWalker(editor, NodeFilter.SHOW_TEXT);
  let acc = 0;
  let last: Text | null = null;
  let n = walker.nextNode();
  while (n) {
    const node = n as Text;
    const len = (node.textContent || '').length;
    if (target <= acc + len) return { node, offset: Math.max(0, target - acc) };
    acc += len;
    last = node;
    n = walker.nextNode();
  }
  if (last) return { node: last, offset: (last.textContent || '').length };
  return null;
}

/** 按文本节点累计偏移构造 Range；越界返回 null。 */
export function rangeFromTextOffsets(editor: HTMLElement, start: number, end: number): Range | null {
  const text = editorPlainText(editor);
  if (!Number.isFinite(start) || !Number.isFinite(end)) return null;
  if (start < 0 || end < start || end > text.length) return null;
  const s = locateTextOffset(editor, start);
  const e = locateTextOffset(editor, end);
  if (!s || !e) return null;
  try {
    const r = document.createRange();
    r.setStart(s.node, s.offset);
    r.setEnd(e.node, e.offset);
    return r;
  } catch {
    return null;
  }
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
