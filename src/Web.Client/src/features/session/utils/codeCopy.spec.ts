/** 代码块复制：渲染注入、事件委托、反馈态与边界的单测。 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderMarkdown } from './markdownRender';
import { renderBlockHtml, renderFallbackHtml } from './markdownBlocks';

const { copyMock } = vi.hoisted(() => ({ copyMock: vi.fn() }));
vi.mock('./historyAnchor', () => ({
  copyTextToClipboard: copyMock,
}));

import { bindCodeCopyDelegation, flashCopied } from './codeCopyDelegation';

function mountHtml(html: string): HTMLElement {
  const el = document.createElement('div');
  el.innerHTML = html;
  document.body.appendChild(el);
  return el;
}

beforeEach(() => {
  copyMock.mockReset();
  copyMock.mockResolvedValue(true);
});

describe('代码块复制按钮（渲染注入）', () => {
  it('fence 代码块注入按钮且保留 pre/code 结构', () => {
    const html = renderMarkdown('```ts\nconst a=1\n```');
    expect(html).toContain('data-code-copy');
    expect(html).toContain('md-code-shell');
    expect(html).toContain('<pre>');
    expect(html).toContain('<code class="language-ts">');
  });

  it('无语言与未知语言块也注入按钮', () => {
    expect(renderMarkdown('```\nplain\n```')).toContain('data-code-copy');
    expect(renderMarkdown('```unknownlang\nx\n```')).toContain('data-code-copy');
  });

  it('流式与稳态共用渲染器，代码块结构一致', () => {
    const src = '```ts\nconst a=1\n```';
    const stable = renderMarkdown(src);
    const stream = renderBlockHtml(src);
    expect(stable).toContain('data-code-copy');
    expect(stream).toContain('data-code-copy');
    expect(stable).toContain('<pre>');
    expect(stream).toContain('<pre>');
    expect(stable.match(/hljs-[a-z-]+/g)).toEqual(stream.match(/hljs-[a-z-]+/g));
  });

  it('超限降级块无复制按钮', () => {
    const html = renderMarkdown('```js\n' + 'const a=1;\n'.repeat(12000) + '```');
    expect(html).toContain('markdown-fallback');
    expect(html).not.toContain('data-code-copy');
  });

  it('降级渲染函数产物无复制按钮', () => {
    expect(renderFallbackHtml('x', 'source-text')).not.toContain('data-code-copy');
  });
});

describe('代码块复制（事件委托）', () => {
  it('点击按钮复制原始代码且只调用双通道工具一次', () => {
    const container = mountHtml(renderMarkdown('```ts\nconst a = 1;\n```'));
    const unbind = bindCodeCopyDelegation(container);
    const button = container.querySelector<HTMLElement>('[data-code-copy]');
    expect(button).not.toBeNull();
    button!.click();
    expect(copyMock).toHaveBeenCalledTimes(1);
    // fence 的 code content 含原始结尾换行，复制的正是这段未经高亮污染的原文。
    expect(copyMock).toHaveBeenCalledWith('const a = 1;\n');
    unbind();
    container.remove();
  });

  it('复制文本取自 code.textContent，不含高亮 span', async () => {
    const container = mountHtml(renderMarkdown('```js\nconst x = 1;\n```'));
    const unbind = bindCodeCopyDelegation(container);
    const code = container.querySelector('pre code') as HTMLElement;
    expect(code.querySelector('span')).not.toBeNull();
    (container.querySelector('[data-code-copy]') as HTMLElement).click();
    await Promise.resolve();
    const copied = copyMock.mock.calls[0][0] as string;
    expect(copied).not.toContain('<span');
    expect(copied).toContain('const x = 1;');
    unbind();
    container.remove();
  });

  it('点击不冒泡到 window', () => {
    const windowSpy = vi.fn();
    window.addEventListener('click', windowSpy);
    const container = mountHtml(renderMarkdown('```js\nconst x=1;\n```'));
    const unbind = bindCodeCopyDelegation(container);
    (container.querySelector('[data-code-copy]') as HTMLElement).click();
    expect(windowSpy).not.toHaveBeenCalled();
    window.removeEventListener('click', windowSpy);
    unbind();
    container.remove();
  });

  it('空代码块点击不调用复制', () => {
    const container = mountHtml('<div class="md-code-shell"><button data-code-copy></button><pre><code></code></pre></div>');
    const unbind = bindCodeCopyDelegation(container);
    (container.querySelector('[data-code-copy]') as HTMLElement).click();
    expect(copyMock).not.toHaveBeenCalled();
    unbind();
    container.remove();
  });

  it('解绑后移除监听器', () => {
    const container = document.createElement('div');
    const removeSpy = vi.spyOn(container, 'removeEventListener');
    const unbind = bindCodeCopyDelegation(container);
    unbind();
    expect(removeSpy).toHaveBeenCalledWith('click', expect.any(Function));
    removeSpy.mockRestore();
  });
});

describe('代码块复制（反馈态）', () => {
  it('复制成功后切已复制文案并在短暂后恢复', () => {
    vi.useFakeTimers();
    try {
      const button = document.createElement('button');
      button.innerHTML = '<span class="md-code-copy-label">复制</span>';
      flashCopied(button);
      expect(button.getAttribute('data-copied')).toBe('true');
      expect(button.querySelector('.md-code-copy-label')?.textContent).toBe('已复制');
      vi.advanceTimersByTime(1200);
      expect(button.hasAttribute('data-copied')).toBe(false);
      expect(button.querySelector('.md-code-copy-label')?.textContent).toBe('复制');
    } finally {
      vi.useRealTimers();
    }
  });

  it('复制失败不触发反馈', async () => {
    copyMock.mockResolvedValue(false);
    const container = mountHtml(renderMarkdown('```js\nconst x=1;\n```'));
    const unbind = bindCodeCopyDelegation(container);
    const button = container.querySelector('[data-code-copy]') as HTMLElement;
    button.click();
    await Promise.resolve();
    await Promise.resolve();
    expect(button.hasAttribute('data-copied')).toBe(false);
    unbind();
    container.remove();
  });
});
