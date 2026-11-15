/** A档富粘贴去样式兜底验收（jsdom）：onPaste 四分支 + normalizePasteText。 */
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { computed, ref } from 'vue';
import { useComposerAttachments, type AttachmentDeps } from '@features/agent/composables/useComposerAttachments';
import { createEmptyDraft, MAX_PASTE_TEXT } from '@shared/utils/composerModel';
import { normalizePasteText } from '@shared/utils/clipboardImage';

// jsdom 无 URL.createObjectURL（图片 pill 预览用），此处打桩避免 addFile 抛错。
if (typeof URL.createObjectURL !== 'function') {
  (URL as unknown as { createObjectURL: (o: unknown) => string }).createObjectURL = () => 'blob:mock';
}
if (typeof URL.revokeObjectURL !== 'function') {
  (URL as unknown as { revokeObjectURL: (u: string) => void }).revokeObjectURL = () => undefined;
}

function makeDeps() {
  const inserted: string[] = [];
  const warned: string[] = [];
  const added: File[] = [];
  const deps: AttachmentDeps = {
    editorRef: ref<HTMLElement | null>(null),
    fileRef: ref<HTMLInputElement | null>(null),
    isGenerating: computed(() => false),
    askMasked: ref(false),
    getDraft: () => createEmptyDraft(),
    setDraft: () => undefined,
    readEditorToDraft: () => undefined,
    insertPillAtCaret: () => undefined,
    insertTextAtCaret: (t: string) => {
      inserted.push(t);
    },
    warn: (m: string) => {
      warned.push(m);
    },
    error: () => undefined,
    setDragOver: () => undefined,
  };
  return { deps, inserted, warned, added, api: useComposerAttachments(deps) };
}

function pasteEvent(text: string, html: string, files: File[] = []): ClipboardEvent {
  const cd = {
    getData: (fmt: string) => (fmt === 'text/plain' ? text : fmt === 'text/html' ? html : ''),
    items: [] as unknown as DataTransferItemList,
    files,
  };
  return {
    clipboardData: cd,
    preventDefault: vi.fn(),
    stopPropagation: vi.fn(),
  } as unknown as ClipboardEvent;
}

function fileOf(name: string, type: string, size: number): File {
  return new File([new Uint8Array(size)], name, { type });
}

describe('normalizePasteText（纯函数）', () => {
  it('N1 \\r\\n→\\n、孤立\\r→\\n、去\\0，保留缩进不 trim', () => {
    expect(normalizePasteText('a\r\nb\rc\0d')).toBe('a\nb\ncd');
    expect(normalizePasteText('  code\n    indent  ')).toBe('  code\n    indent  ');
  });
});

describe('onPaste 富粘贴去样式兜底（A档）', () => {
  beforeEach(() => {
    vi.useRealTimers();
  });

  it('P1 Word 样式文本（<b><span style>）⇒ preventDefault + 只插 text/plain，html 不进 DOM', () => {
    const { api, deps, inserted } = makeDeps();
    const html = '<b>粗体</b><span style="background:yellow">高亮</span>';
    const e = pasteEvent('粗体高亮', html);
    api.onPaste(e);
    expect(e.preventDefault).toHaveBeenCalled();
    expect(e.stopPropagation).toHaveBeenCalled();
    expect(inserted).toEqual(['粗体高亮']);
    // 口径：insertTextAtCaret 入参必须不含任何标签
    expect(inserted[0]).not.toContain('<');
    void deps;
  });

  it('P2 超 20000 字 ⇒ 截断 + warn 一次（文案口径不变）', () => {
    const { api, inserted, warned } = makeDeps();
    const long = 'x'.repeat(MAX_PASTE_TEXT + 5);
    const e = pasteEvent(long, '');
    api.onPaste(e);
    expect(e.preventDefault).toHaveBeenCalled();
    expect(inserted).toEqual([long.slice(0, MAX_PASTE_TEXT)]);
    expect(warned).toEqual([`粘贴文本超 ${MAX_PASTE_TEXT} 字符，已截断`]);
  });

  it('P3 本地 png 文件 ⇒ addFile 走 pill，不走文本兜底（text 为空时不插文本）', () => {
    const { api, inserted } = makeDeps();
    // 用 insertPill 路径会调 URL.createObjectURL（已打桩）；text 为空 ⇒ inserted 为空
    const f = fileOf(`p3-${Date.now()}.png`, 'image/png', 2048);
    const e = pasteEvent('', '', [f]);
    api.onPaste(e);
    expect(e.preventDefault).toHaveBeenCalled();
    expect(inserted).toEqual([]);
  });

  it('P4 data:image/png 内联图 html ⇒ 转 pill（pasteHtmlImage 成功即 return，不进纯文本分支）', () => {
    const { api, inserted } = makeDeps();
    const tiny = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==';
    const html = `<img src="data:image/png;base64,${tiny}">`;
    const e = pasteEvent('', html);
    api.onPaste(e);
    expect(e.preventDefault).toHaveBeenCalled();
    // data 图不插纯文本（图片走 pill）
    expect(inserted).toEqual([]);
  });

  it('P5 不支持的 data:image/gif ⇒ pasteHtmlImage 失败则吞默认行为并 fallthrough（text 非空才插文本）', () => {
    const { api, inserted } = makeDeps();
    const html = '<img src="data:image/gif;base64,R0lGODdhAQABAIAAAP///////ywAAAAAAQABAAACAkQBADs=">';
    const e = pasteEvent('alt 文案', html);
    api.onPaste(e);
    expect(e.preventDefault).toHaveBeenCalled();
    expect(e.stopPropagation).toHaveBeenCalled();
    // fallthrough 到纯文本分支：只插 text/plain，浏览器破图被吞掉
    expect(inserted).toEqual(['alt 文案']);
  });

  it('P6 空粘贴（text/html 均空、无文件）⇒ 三者皆不调，不 preventDefault', () => {
    const { api, inserted, warned } = makeDeps();
    const e = pasteEvent('', '');
    api.onPaste(e);
    expect(e.preventDefault).not.toHaveBeenCalled();
    expect(inserted).toEqual([]);
    expect(warned).toEqual([]);
  });
});
