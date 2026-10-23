/** M1-1/§1.1(d)：稳态整源渲染 renderMarkdown 直测（补齐已知覆盖盲区）。 */
import { describe, expect, it } from 'vitest';
import { renderMarkdown } from '@features/session/utils/markdownRender';
import { sharedMd } from '@features/session/utils/markdownBlocks';

const hljsTokens = (html: string): string[] => (html.match(/hljs-[a-z-]+/g) || []).sort();

describe('renderMarkdown（稳态整源渲染）', () => {
  it('普通段落产出 <p>', () => {
    expect(renderMarkdown('正文段落')).toContain('<p>');
  });

  it('代码块含 language 时产出 hljs token', () => {
    const html = renderMarkdown('```js\nconst x = 1;\n```\n');
    expect(html).toContain('<pre>');
    expect(html).toContain('hljs-keyword');
  });

  it('切块渲染与整源渲染的 token 结构一致（交接零跳变的前提）', () => {
    // 流式态按块渲染，稳态按块渲染；只要「块切分不改变 token 结构」，
    // 活动容器 → 历史区的交接就不会出现代码块重排。
    const src = '# 标题\n\n```ts\nconst a: number = 1;\n```\n\n- 列表项\n\n```js\nlet b = "s";\n```\n';
    const tokens = hljsTokens(renderMarkdown(src));
    const whole = hljsTokens(sharedMd.render(src));
    expect(tokens.length).toBeGreaterThan(0);
    expect(tokens).toEqual(whole);
  });

  it('超过 128KB 降级为 <pre> 并带提示', () => {
    const html = renderMarkdown('x'.repeat(128 * 1024 + 1));
    expect(html).toContain('<pre>');
    expect(html).toContain('内容超长');
  });

  it('多条不同消息交替调用不会因缓存互相污染', () => {
    const a = renderMarkdown('# A\n\n```js\nconst a=1;\n```');
    const b = renderMarkdown('# B\n\n```py\nb=2\n```');
    expect(a).toContain('A');
    expect(a).not.toContain('B');
    expect(b).toContain('B');
    // 再调一次 a，必须与首次结果一致（前置清理修掉的「共用 default 缓存键」缺陷）
    expect(renderMarkdown('# A\n\n```js\nconst a=1;\n```')).toBe(a);
  });
});
