/** need22-F02：编辑器 DOM 层 skill pill 单测（frontend/02 §3.6 D1~D9，jsdom）。 */
import { describe, expect, it } from 'vitest';
import {
  backspacePill,
  createSkillPillEl,
  insertSkillPill,
  readSegments,
  renderDraft,
  replaceRangeWithPill,
} from '@features/agent/composables/composerEditorDom';

function makeEditor(): HTMLElement {
  const el = document.createElement('div');
  el.contentEditable = 'true';
  document.body.appendChild(el);
  return el;
}
function setCaret(node: Node, offset: number): void {
  const sel = window.getSelection();
  if (!sel) return;
  const r = document.createRange();
  r.setStart(node, offset);
  r.collapse(true);
  sel.removeAllRanges();
  sel.addRange(r);
}

describe('composerEditorDom skill pill（D1~D9）', () => {
  it('D1 createSkillPillEl ⇒ data-skill / contentEditable=false / scope 徽标 / ×', () => {
    const pill = createSkillPillEl('global', 'foo');
    expect(pill.getAttribute('data-skill')).toBe('global/foo');
    expect(pill.contentEditable).toBe('false');
    expect(pill.className).toBe('skill-tag');
    expect(pill.getAttribute('data-testid')).toBe('skill-pill-global-foo');
    expect(pill.querySelector('.skill-scope')?.textContent).toBe('global');
    expect(pill.querySelector('.skill-name')?.textContent).toBe('foo');
    const x = pill.querySelector('.skill-x');
    expect(x?.getAttribute('data-x')).toBe('skill:global/foo');
    expect(x?.textContent).toBe('×');
  });

  it('D2 readSegments：文本 + skill pill ⇒ 段序列 [text, skill]', () => {
    const editor = makeEditor();
    editor.appendChild(document.createTextNode('前'));
    editor.appendChild(createSkillPillEl('project', 'bar'));
    const segs = readSegments(editor, () => false);
    expect(segs).toEqual([
      { kind: 'text', text: '前' },
      { kind: 'skill', scope: 'project', name: 'bar' },
    ]);
  });

  it('D3 readSegments：pill 内 × 文本不进入 text 段', () => {
    const editor = makeEditor();
    editor.appendChild(createSkillPillEl('global', 'foo'));
    const segs = readSegments(editor, () => false);
    const text = segs
      .filter((s) => s.kind === 'text')
      .map((s) => (s as { text: string }).text)
      .join('');
    expect(text).not.toContain('×');
    expect(text).not.toContain('global');
    expect(text).not.toContain('foo');
  });

  it('D4 readSegments：pill 前后都有文本 ⇒ 前后各自成段且顺序保持', () => {
    const editor = makeEditor();
    editor.appendChild(document.createTextNode('前'));
    editor.appendChild(createSkillPillEl('global', 'foo'));
    editor.appendChild(document.createTextNode('后'));
    const segs = readSegments(editor, () => false);
    expect(segs.map((s) => s.kind)).toEqual(['text', 'skill', 'text']);
    expect(segs[0]).toEqual({ kind: 'text', text: '前' });
    expect(segs[2]).toEqual({ kind: 'text', text: '后' });
  });

  it('D5 insertSkillPill ⇒ DOM 出现 pill 且 caret 在 pill 之后', () => {
    const editor = makeEditor();
    editor.appendChild(document.createTextNode('ab'));
    setCaret(editor.firstChild as Text, 2);
    insertSkillPill(editor, 'global', 'foo');
    const pill = editor.querySelector('[data-skill="global/foo"]');
    expect(pill).toBeTruthy();
    const sel = window.getSelection();
    const idx = Array.from(editor.childNodes).indexOf(pill as ChildNode);
    expect(sel?.anchorNode).toBe(editor);
    expect(sel?.anchorOffset).toBe(idx + 1);
  });

  it('D6 replaceRangeWithPill ⇒ 选中的 /que 文本被 pill 替换', () => {
    const editor = makeEditor();
    const textNode = document.createTextNode('前缀 /que 后缀');
    editor.appendChild(textNode);
    const r = document.createRange();
    r.setStart(textNode, 3);
    r.setEnd(textNode, 7);
    replaceRangeWithPill(editor, r, createSkillPillEl('global', 'foo'));
    expect(editor.textContent).not.toContain('/que');
    expect(editor.querySelector('[data-skill="global/foo"]')).toBeTruthy();
    expect(editor.textContent).toContain('前缀');
    expect(editor.textContent).toContain('后缀');
  });

  it('D7 backspacePill：光标在 skill pill 后文本首字符 ⇒ 返回该 pill', () => {
    const editor = makeEditor();
    const pill = createSkillPillEl('global', 'foo');
    editor.appendChild(pill);
    const tail = document.createTextNode('abc');
    editor.appendChild(tail);
    setCaret(tail, 0);
    expect(backspacePill(editor)).toBe(pill);
  });

  it('D8 backspacePill：光标在图片 pill 后 ⇒ 仍返回图片 pill（回归）', () => {
    const editor = makeEditor();
    const img = document.createElement('span');
    img.setAttribute('data-att', 'att_x');
    editor.appendChild(img);
    const tail = document.createTextNode('abc');
    editor.appendChild(tail);
    setCaret(tail, 0);
    expect(backspacePill(editor)).toBe(img);
  });

  it('D9 renderDraft 段序 ⇒ text → skill → text 顺序还原', () => {
    const editor = makeEditor();
    renderDraft(
      editor,
      [
        { kind: 'text', text: '前' },
        { kind: 'skill', scope: 'global', name: 'foo' },
        { kind: 'text', text: '后' },
      ],
      {}
    );
    const kinds = Array.from(editor.children).map((c) => (c.getAttribute('data-skill') ? 'skill' : 'text'));
    expect(kinds).toEqual(['text', 'skill', 'text']);
    expect(editor.children[0].textContent).toBe('前');
    expect(editor.children[2].textContent).toBe('后');
  });
});
