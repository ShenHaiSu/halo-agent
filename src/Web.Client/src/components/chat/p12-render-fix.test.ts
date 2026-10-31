// P12 render-fix 结构回归：列表/表格 AST → MessageBody VNode 结构稳定。
// 样式层断言见 CSS 文本检查（jsdom 无完整 computed list-style/table 边框计算）。
import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { mount } from '@vue/test-utils';
import { parseMarkdown } from './MarkdownRenderer';
import MessageBody from './MessageBody.vue';

function mountBody(md: string) {
  const nodes = parseMarkdown(md);
  return { w: mount(MessageBody, { props: { nodes } }), nodes };
}

describe('P12 列表结构', () => {
  it('单级 ul/ol 渲染为 ul.md-list / ol.md-list', () => {
    const u = mountBody('- a\n- b\n');
    expect(u.w.find('ul.md-list').exists()).toBe(true);
    const o = mountBody('1. a\n2. b\n');
    expect(o.w.find('ol.md-list').exists()).toBe(true);
    u.w.unmount(); o.w.unmount();
  });

  it('ol start=5/0 以 String(n.start) 透传', () => {
    const w5 = mountBody('5. a\n6. b\n');
    expect(w5.w.find('ol.md-list').attributes('start')).toBe('5');
    const w0 = mountBody('0. a\n1. b\n');
    // start=0 同样透传（与 MessageBody.vue:117 String(n.start) 一致）
    expect(w0.w.find('ol.md-list').attributes('start')).toBe('0');
    w5.w.unmount(); w0.w.unmount();
  });

  it('任务列表 li 带 md-li-task + checkbox', () => {
    const w = mountBody('- [ ] todo\n- [x] done\n');
    const tasks = w.w.findAll('li.md-li-task');
    expect(tasks.length).toBe(2);
    expect(w.w.findAll('li.md-li-task input.md-task[type="checkbox"]').length).toBe(2);
    w.w.unmount();
  });

  it('嵌套列表保留层级 DOM（ul>li>ul）', () => {
    const w = mountBody('- a\n  - a1\n    - a2\n');
    const nested = w.w.find('ul.md-list li ul.md-list');
    expect(nested.exists()).toBe(true);
    w.w.unmount();
  });

  it('ul⊂ol 混排保留层级 DOM', () => {
    const w = mountBody('1. a\n   - b\n');
    expect(w.w.find('ol.md-list li ul.md-list').exists()).toBe(true);
    w.w.unmount();
  });
});

describe('P12 表格结构', () => {
  it('表结构为 wrap > table.md-table > thead/tbody', () => {
    const w = mountBody('| a | b |\n|---|---|\n| 1 | 2 |\n');
    expect(w.w.find('.md-table-wrap > table.md-table').exists()).toBe(true);
    expect(w.w.find('table.md-table > thead').exists()).toBe(true);
    expect(w.w.find('table.md-table > tbody').exists()).toBe(true);
    w.w.unmount();
  });

  it('对齐列内联 text-align 覆盖', () => {
    const w = mountBody('| a | b | c |\n|:--|:--:|--:|\n| 1 | 2 | 3 |\n');
     const ths = w.w.findAll('table.md-table thead th');
     expect(ths[0]!.attributes('style')).toContain('text-align: left');
     expect(ths[1]!.attributes('style')).toContain('text-align: center');
     expect(ths[2]!.attributes('style')).toContain('text-align: right');
     w.w.unmount();
  });
});

 describe('P12 CSS 文本（作用域自闭环）', () => {
   const vue = readFileSync(resolve(__dirname, 'MessageBody.vue'), 'utf-8');
   const css = vue.slice(vue.indexOf('<style'));
   it('源码无 deep 写法', () => {
     expect(css).not.toContain(':deep(');
   });
   it('列表：disc/decimal + 1.6em 基数 + task -1.6em（裸 scoped）', () => {
     // P13：pushScopeId 绑定后内层自带 data-v，裸写 scoped 即可。
     expect(css).toContain('.md-list{margin:0 0 var(--space-3);padding-inline-start:1.6em;list-style:disc outside none;}');
     expect(css).toContain('ol.md-list{list-style-type:decimal;}');
     expect(css).toContain('margin-inline-start:-1.6em');
     expect(css).toContain('lower-alpha');
     expect(css).toContain('lower-roman');
   });
   it('表格：wrap 外框 + 去边 + 8px/12px 内边距（裸 scoped）', () => {
     expect(css).toContain('.md-table-wrap{overflow-x:auto;');
     expect(css).toContain('max-width:100%');
     expect(css).toContain('padding:var(--space-2) var(--space-3)');
     expect(css).toContain('border-top:0');
     expect(css).toContain('border-bottom:0');
   });
   it('内层 ul/table 自带 scopeId 且与根一致（R2 核心）', () => {
     const w = mount(MessageBody, { props: { nodes: parseMarkdown('- a\n\n| x |\n|---|\n| 1 |\n') } });
     const root = w.find('.md-body');
     const rootAttrs = root.attributes();
     const scopeKey = Object.keys(rootAttrs).find((k) => k.startsWith('data-v'));
     expect(scopeKey).toBeDefined();
     const scopeVal = rootAttrs[scopeKey!] ?? '';
     const ul = root.find('ul.md-list');
     expect(ul.exists()).toBe(true);
     expect(ul.attributes()[scopeKey!]).toBe(scopeVal);
     const table = root.find('.md-table-wrap > table.md-table');
     expect(table.exists()).toBe(true);
     expect(table.attributes()[scopeKey!]).toBe(scopeVal);
     w.unmount();
   });
   it('fence CodeBlock 根继承父 scope 属预期', () => {
     const w = mount(MessageBody, { props: { nodes: parseMarkdown('```js\nconst a = 1;\n```\n') } });
     const root = w.find('.md-body');
     const scopeKey = Object.keys(root.attributes()).find((k) => k.startsWith('data-v'));
     expect(scopeKey).toBeDefined();
     const scopeVal = root.attributes()[scopeKey!] ?? '';
     const codeRoot = root.find('.code-block');
     expect(codeRoot.exists()).toBe(true);
     // 父 scope 以 slot-scope 形式继承到子组件根，属标准行为；子组件自身 scoped 不受影响。
     expect(codeRoot.attributes()[scopeKey!]).toBe(scopeVal);
     w.unmount();
   });
 });

describe('P12 import 治理', () => {
  const root = resolve(__dirname, '../../styles');
  it('index.css 接回 chat.css；chat.css 无 .md-table-wrap 重复', () => {
    const index = readFileSync(resolve(root, 'index.css'), 'utf-8');
    const chat = readFileSync(resolve(root, 'chat.css'), 'utf-8');
    expect(index).toContain(`@import './chat.css';`);
    expect(chat).not.toContain('.md-table-wrap');
  });
});
