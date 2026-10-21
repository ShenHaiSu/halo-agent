/** need22-F03：斜杠面板单测（frontend/03 §10 S1~S13，jsdom）。 */
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { ref } from 'vue';
import { detectSlashTrigger, useSlashMenu } from '@features/agent/composables/useSlashMenu';
import { registerSlashProvider, unregisterSlashProvider, type SlashCandidate } from '@shared/utils/slashRegistry';
import { createEmptyDraft, type ComposerDraft } from '@shared/utils/composerModel';

const PROVIDER_ID = 'skill-test-provider';
const ALL: SlashCandidate[] = [
  { id: 'skill:global:alpha', providerId: 'skill', scope: 'global', prefix: 'global/', label: 'global/alpha' },
  { id: 'skill:project:beta', providerId: 'skill', scope: 'project', prefix: 'project/', label: 'project/beta' },
];

function editorWith(text: string, caretOffset?: number): HTMLElement {
  const el = document.createElement('div');
  el.contentEditable = 'true';
  document.body.appendChild(el);
  const node = document.createTextNode(text);
  el.appendChild(node);
  const off = caretOffset ?? text.length;
  const r = document.createRange();
  r.setStart(node, off);
  r.collapse(true);
  const sel = window.getSelection();
  sel?.removeAllRanges();
  sel?.addRange(r);
  return el;
}

function makeApi(draft: ComposerDraft = createEmptyDraft()) {
  const editorRef = ref<HTMLElement | null>(null);
  const api = useSlashMenu({
    editorRef,
    readEditorToDraft: () => undefined,
    getDraft: () => draft,
  });
  return { api, editorRef, draft };
}

async function openPanel(text: string, draft?: ComposerDraft) {
  const editor = editorWith(text);
  const { api, editorRef, draft: d } = makeApi(draft);
  editorRef.value = editor;
  api.refresh(editor);
  await vi.waitFor(() => expect(api.loading.value).toBe(false));
  return { api, editor, draft: d };
}

beforeAll(() => {
  registerSlashProvider({ id: PROVIDER_ID, order: 1, load: () => ALL });
});
afterAll(() => {
  unregisterSlashProvider(PROVIDER_ID);
});

describe('detectSlashTrigger（S1~S6）', () => {
  it('S1 行首 "/" ⇒ 命中且 query 为空', () => {
    const editor = editorWith('/');
    const info = detectSlashTrigger(editor);
    expect(info).not.toBeNull();
    expect(info?.query).toBe('');
    expect(info?.start).toBe(0);
    expect(info?.end).toBe(1);
  });

  it('S2 空格后 "/ab" ⇒ 命中且 query="ab"', () => {
    const editor = editorWith('a /ab');
    const info = detectSlashTrigger(editor);
    expect(info?.query).toBe('ab');
    expect(info?.start).toBe(2);
    expect(info?.end).toBe(5);
  });

  it('S3 "a/b" ⇒ 不命中', () => {
    expect(detectSlashTrigger(editorWith('a/b'))).toBeNull();
  });

  it('S4 "http://x" ⇒ 不命中（URL 排除）', () => {
    expect(detectSlashTrigger(editorWith('http://x'))).toBeNull();
  });

  it('S5 query 含空格 ⇒ 不命中', () => {
    expect(detectSlashTrigger(editorWith('/ab cd'))).toBeNull();
  });

  it('S6 光标不在编辑器内 ⇒ null', () => {
    const other = editorWith('/abc');
    const outside = document.createElement('div');
    document.body.appendChild(outside);
    const node = document.createTextNode('x');
    outside.appendChild(node);
    const r = document.createRange();
    r.setStart(node, 0);
    r.collapse(true);
    const sel = window.getSelection();
    sel?.removeAllRanges();
    sel?.addRange(r);
    expect(detectSlashTrigger(other)).toBeNull();
  });

  it('词首 "/" 后接路径文本 ⇒ 命中且 query 只取斜杠后连续字符', () => {
    const editor = editorWith('/usr');
    const info = detectSlashTrigger(editor);
    expect(info?.query).toBe('usr');
  });
});

describe('useSlashMenu 键盘与选中（S7~S13）', () => {
  it('S7 handleKeydown ArrowDown ⇒ index 循环回绕', async () => {
    const { api } = await openPanel('/a');
    expect(api.candidates.value.length).toBe(2);
    expect(api.index.value).toBe(0);
    expect(api.handleKeydown(new KeyboardEvent('keydown', { key: 'ArrowDown' }))).toBe(true);
    expect(api.index.value).toBe(1);
    expect(api.handleKeydown(new KeyboardEvent('keydown', { key: 'ArrowDown' }))).toBe(true);
    expect(api.index.value).toBe(0);
    expect(api.handleKeydown(new KeyboardEvent('keydown', { key: 'ArrowUp' }))).toBe(true);
    expect(api.index.value).toBe(1);
  });

  it('S8 handleKeydown Enter（有候选）⇒ 消费事件并生成 pill', async () => {
    const { api, editor } = await openPanel('/a');
    const ev = new KeyboardEvent('keydown', { key: 'Enter', cancelable: true });
    const consumed = api.handleKeydown(ev);
    expect(consumed).toBe(true);
    expect(ev.defaultPrevented).toBe(true);
    expect(editor.querySelector('[data-skill="global/alpha"]')).toBeTruthy();
    expect(api.active.value).toBe(false);
  });

  it('S9 handleKeydown Enter（无候选）⇒ 不消费（照常发送）', async () => {
    const { api } = await openPanel('/zzz');
    expect(api.candidates.value.length).toBe(0);
    const ev = new KeyboardEvent('keydown', { key: 'Enter', cancelable: true });
    expect(api.handleKeydown(ev)).toBe(false);
    expect(ev.defaultPrevented).toBe(false);
  });

  it('S10 handleKeydown Escape ⇒ 关闭面板', async () => {
    const { api } = await openPanel('/a');
    expect(api.active.value).toBe(true);
    expect(api.handleKeydown(new KeyboardEvent('keydown', { key: 'Escape' }))).toBe(true);
    expect(api.active.value).toBe(false);
  });

  it('S11 isComposing ⇒ 一律不消费', async () => {
    const { api } = await openPanel('/a');
    const ev = new KeyboardEvent('keydown', { key: 'Enter' });
    Object.defineProperty(ev, 'isComposing', { value: true });
    expect(api.handleKeydown(ev)).toBe(false);
    expect(api.active.value).toBe(true);
  });

  it('S12 select ⇒ 生成 pill 且 "/query" 文本消失', async () => {
    const { api, editor } = await openPanel('/alpha');
    api.select({ id: 'skill:global:alpha', providerId: 'skill', scope: 'global', label: 'global/alpha' });
    expect(editor.querySelector('[data-skill="global/alpha"]')).toBeTruthy();
    expect(editor.textContent).not.toContain('/alpha');
  });

  it('S13 select：草稿已含同名 skill ⇒ 不重复插入', async () => {
    const draft: ComposerDraft = {
      segments: [{ kind: 'skill', scope: 'global', name: 'alpha' }],
      attachments: {},
    };
    const { api, editor } = await openPanel('/alpha', draft);
    api.select({ id: 'skill:global:alpha', providerId: 'skill', scope: 'global', label: 'global/alpha' });
    expect(editor.querySelectorAll('[data-skill="global/alpha"]').length).toBe(0);
  });
});
