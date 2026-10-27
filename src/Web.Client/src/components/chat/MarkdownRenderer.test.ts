// src/Web.Client/src/components/chat/MarkdownRenderer.test.ts
import { describe, expect, it } from 'vitest';
import { parseMarkdown, parseInline, type MdNode } from './MarkdownRenderer';

/** 取第一个节点的 type（断言用） */
const t = (s: string, opts = {}): string => parseMarkdown(s, opts)[0]?.type ?? 'none';

describe('块级：基础 8 类', () => {
  it('1. 空输入 → []', () => {
    expect(parseMarkdown('')).toEqual([]);
    expect(parseMarkdown('   \n\n  ')).toEqual([]);
  });

  it('2. 段落', () => {
    const n = parseMarkdown('hello') as MdNode[];
    expect(n).toHaveLength(1);
    expect(n[0]!.type).toBe('paragraph');
  });

  it('3. 标题 1~6 级', () => {
    for (let lv = 1; lv <= 6; lv++) {
      const n = parseMarkdown(`${'#'.repeat(lv)} T`);
      expect(n[0]).toEqual({
        type: 'heading',
        level: lv,
        children: [{ type: 'text', value: 'T' }],
      });
    }
  });

  it('4. 7 个 # 不是标题', () => {
    expect(t('####### T')).toBe('paragraph');
  });

  it('5. 分隔线三种写法', () => {
    expect(t('---')).toBe('hr');
    expect(t('***')).toBe('hr');
    expect(t('___')).toBe('hr');
  });

  it('6. 引用', () => {
    const n = parseMarkdown('> quote') as MdNode[];
    expect(n[0]!.type).toBe('blockquote');
  });

  it('7. 引用内嵌套列表', () => {
    const n = parseMarkdown('> - a\n> - b') as MdNode[];
    const bq = n[0] as Extract<MdNode, { type: 'blockquote' }>;
    expect(bq.children[0]!.type).toBe('list');
  });

  it('8. 松散引用（含空行）', () => {
    const n = parseMarkdown('> a\n>\n> b') as MdNode[];
    expect(n).toHaveLength(1);
  });
});

describe('围栏代码块：7 类', () => {
  it('9. 基本围栏', () => {
    const n = parseMarkdown('```ts\nconst a = 1;\n```') as MdNode[];
    expect(n[0]).toEqual({ type: 'fence', lang: 'ts', value: 'const a = 1;', closed: true });
  });

  it('10. 波浪号围栏', () => {
    const n = parseMarkdown('~~~py\nx=1\n~~~') as MdNode[];
    expect(n[0]!.type).toBe('fence');
    expect((n[0] as { lang: string }).lang).toBe('py');
  });

  it('11. 未闭合围栏（非宽容）→ closed:false', () => {
    const n = parseMarkdown('```ts\nconst a = 1;') as MdNode[];
    expect(n[0]!.type).toBe('fence');
    expect((n[0] as { closed: boolean }).closed).toBe(false);
  });

  it('12. 未闭合围栏（宽容）→ paragraph', () => {
    const n = parseMarkdown('```ts\nconst a = 1;', { lenient: true }) as MdNode[];
    expect(n[0]!.type).toBe('paragraph');
  });

  it('13. 代码块内的 # 不当标题', () => {
    const n = parseMarkdown('```\n# not heading\n```') as MdNode[];
    expect(n).toHaveLength(1);
    expect((n[0] as { value: string }).value).toBe('# not heading');
  });

  it('14. 代码块内的 **x** 不当粗体', () => {
    const n = parseMarkdown('```\n**not bold**\n```') as MdNode[];
    expect((n[0] as { value: string }).value).toBe('**not bold**');
  });

  it('15. 代码块内的 ``` 需更长围栏包裹', () => {
    const n = parseMarkdown('````\n```\ninner\n```\n````') as MdNode[];
    expect(n).toHaveLength(1);
    expect((n[0] as { value: string }).value).toBe('```\ninner\n```');
  });
});

describe('列表：8 类', () => {
  it('16. 无序列表', () => {
    const n = parseMarkdown('- a\n- b\n- c') as MdNode[];
    expect(n[0]!.type).toBe('list');
    expect((n[0] as { items: unknown[] }).items).toHaveLength(3);
  });

  it('17. 有序列表起始值', () => {
    const n = parseMarkdown('3. a\n4. b') as MdNode[];
    expect((n[0] as { start: number }).start).toBe(3);
  });

  it('18. 嵌套 3 层', () => {
    const n = parseMarkdown('- a\n  - b\n    - c') as MdNode[];
    const l1 = n[0] as { items: Array<{ children: MdNode[] }> };
    expect(l1.items[0]!.children.some((x) => x.type === 'list')).toBe(true);
  });

  it('19. 任务列表', () => {
    const n = parseMarkdown('- [x] done\n- [ ] todo') as MdNode[];
    const l = n[0] as { items: Array<{ checked: boolean | null }> };
    expect(l.items[0]!.checked).toBe(true);
    expect(l.items[1]!.checked).toBe(false);
  });

  it('20. 列表项的续行', () => {
    const n = parseMarkdown('- first line\n  second line') as MdNode[];
    const l = n[0] as { items: Array<{ inline: Array<{ value?: string }> | null }> };
    const text = l.items[0]!.inline?.map((x) => x.value ?? '').join('') ?? '';
    expect(text).toContain('second line');
  });

  it('21. 列表项内含代码块', () => {
    const n = parseMarkdown('- item\n  ```ts\n  x=1\n  ```') as MdNode[];
    const l = n[0] as { items: Array<{ children: MdNode[] }> };
    expect(l.items[0]!.children.some((x) => x.type === 'fence')).toBe(true);
  });

  it('22. 有序与无序混排分段', () => {
    const n = parseMarkdown('- a\n\n1. b');
    expect(n.length).toBe(2);
  });

  it('23. 松散列表（项间空行）', () => {
    const n = parseMarkdown('- a\n\n- b') as MdNode[];
    expect((n[0] as { loose: boolean }).loose).toBe(true);
  });
});

describe('表格：4 类', () => {
  it('24. 基本表格', () => {
    const n = parseMarkdown('| a | b |\n| --- | --- |\n| 1 | 2 |') as MdNode[];
    const tb = n[0] as { type: string; head: unknown[]; rows: unknown[] };
    expect(tb.type).toBe('table');
    expect(tb.head).toHaveLength(2);
    expect(tb.rows).toHaveLength(1);
  });

  it('25. 对齐 4 种', () => {
    const n = parseMarkdown(
      '| a | b | c | d |\n| --- | :-- | --: | :-: |\n| 1 | 2 | 3 | 4 |'
    ) as MdNode[];
    expect((n[0] as { align: unknown[] }).align).toEqual([null, 'left', 'right', 'center']);
  });

  it('26. 单元格内的 | 转义', () => {
    const n = parseMarkdown('| a |\n| --- |\n| x\\|y |') as MdNode[];
    const tb = n[0] as { rows: Array<Array<Array<{ value?: string }>>> };
    expect(tb.rows[0]![0]![0]!.value).toBe('x|y');
  });

  it('27. 列数不一致时不认表格', () => {
    expect(t('| a | b |\n| --- |\n| 1 | 2 |')).toBe('paragraph');
  });
});

describe('行内：12 类', () => {
  it('28. 粗体', () => {
    expect(parseInline('**x**')).toEqual([
      { type: 'strong', children: [{ type: 'text', value: 'x' }] },
    ]);
  });

  it('29. 斜体', () => {
    expect(parseInline('*x*')[0]!.type).toBe('em');
  });

  it('30. 粗斜体', () => {
    const n = parseInline('***x***');
    expect(n[0]!.type).toBe('strong');
  });

  it('31. 删除线', () => {
    expect(parseInline('~~x~~')[0]!.type).toBe('del');
  });

  it('32. 行内代码', () => {
    expect(parseInline('`a*b*c`')).toEqual([{ type: 'code', value: 'a*b*c' }]);
  });

  it('33. a_b_c 不当斜体', () => {
    const n = parseInline('a_b_c');
    expect(n).toHaveLength(1);
    expect(n[0]!.type).toBe('text');
  });

  it('34. 转义生效', () => {
    expect(parseInline('\\*not em\\*')).toEqual([{ type: 'text', value: '*not em*' }]);
  });

  it('35. 未闭合粗体当文本', () => {
    const n = parseInline('**unclosed');
    expect(n.every((x) => x.type === 'text')).toBe(true);
  });

  it('36. 单换行 → br', () => {
    const n = parseInline('a\nb');
    expect(n.some((x) => x.type === 'br')).toBe(true);
  });

  it('37. 裸 URL 自动链接', () => {
    const n = parseInline('see https://example.com/x for more');
    expect(n.some((x) => x.type === 'link')).toBe(true);
  });

  it('38. 链接文字里可有格式', () => {
    const n = parseInline('[**bold**](https://a.com)');
    const link = n[0] as { type: string; children: Array<{ type: string }> };
    expect(link.type).toBe('link');
    expect(link.children[0]!.type).toBe('strong');
  });

  it('39. 代码内的星号不被强调（跨区间）', () => {
    const n = parseInline('**a `b*c` d**');
    const strong = n[0] as { type: string; children: Array<{ type: string }> };
    expect(strong.type).toBe('strong');
    expect(strong.children.some((c) => c.type === 'code')).toBe(true);
  });
});

describe('安全：6 类注入', () => {
  it('40a. javascript: 链接只渲染文字', () => {
    const n = parseInline('[click](javascript:alert(1))');
    expect(n.every((x) => x.type !== 'link')).toBe(true);
    expect(n.map((x) => ('value' in x ? x.value : '')).join('')).toContain('click');
  });

  it('40b. JaVaScRiPt: 大小写绕过被拦', () => {
    const n = parseInline('[x](JaVaScRiPt:alert(1))');
    expect(n.every((x) => x.type !== 'link')).toBe(true);
  });

  it('40c. 带控制字符的 javascript: 被拦', () => {
    const n = parseInline('[x](java\tscript:alert(1))');
    expect(n.every((x) => x.type !== 'link')).toBe(true);
  });

  it('40d. file:// 图片不渲染', () => {
    const n = parseInline('![x](file:///C:/Windows/win.ini)');
    expect(n.every((x) => (x as { isImage?: boolean }).isImage !== true)).toBe(true);
  });

  it('40e. data:text/html 图片不渲染', () => {
    const n = parseInline('![x](data:text/html,<script>alert(1)</script>)');
    expect(n.every((x) => (x as { isImage?: boolean }).isImage !== true)).toBe(true);
  });

  it('40f. data:image/png 图片正常渲染', () => {
    const n = parseInline('![x](data:image/png;base64,iVBORw0KGgo=)');
    expect(n.some((x) => (x as { isImage?: boolean }).isImage === true)).toBe(true);
  });
});

describe('健壮性：4 类', () => {
  it('41. 不含粗体时的极长单行不崩', () => {
    const s = 'a'.repeat(100_000);
    expect(() => parseMarkdown(s)).not.toThrow();
  });

  it('42. 超 maxChars 时截断并提示', () => {
    const s = 'x'.repeat(300_000);
    const n = parseMarkdown(s, { maxChars: 1000 });
    const last = n[n.length - 1] as { children: Array<{ value: string }> };
    expect(last.children[0]!.value).toContain('已截断');
  });

  it('43. 深度嵌套不栈溢出', () => {
    const s = '> '.repeat(30) + 'deep';
    expect(() => parseMarkdown(s)).not.toThrow();
  });

  it('44. 只有空白与换行', () => {
    expect(parseMarkdown('\n\n\n   \n')).toEqual([]);
  });
});
