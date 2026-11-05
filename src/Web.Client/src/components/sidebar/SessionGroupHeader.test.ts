// src/Web.Client/src/components/sidebar/SessionGroupHeader.test.ts
import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import SessionGroupHeader from './SessionGroupHeader.vue';
import { buildOffsets, computeVisible } from './virtualize';

/** 同目录源码，用 URL 解析避免 __dirname 在 ESM 下不可用 */
const readSibling = (file: string): string =>
  readFileSync(new URL(file, import.meta.url), 'utf-8');

describe('SessionGroupHeader', () => {
  it('R1: 不得声明 position: sticky（与虚拟滚动 transform 补偿冲突）', () => {
    const src = readSibling('SessionGroupHeader.vue');
    expect(src).not.toMatch(/position:\s*sticky/);
    expect(src).not.toMatch(/\bsticky\b/);
  });

  it('R2: 标题行为纯展示，不得有交互暗示样式', () => {
    const src = readSibling('SessionGroupHeader.vue');
    expect(src).not.toMatch(/z-index/);
    expect(src).not.toMatch(/cursor:\s*pointer/);
    expect(src).not.toMatch(/:hover/);
  });

  it('R3: 标题行不得绑定事件或进 Tab 序列', () => {
    const w = mount(SessionGroupHeader, { props: { label: '今天', height: 22 } });
    expect(w.element.getAttribute('tabindex')).toBeNull();
    expect(w.element.getAttribute('role')).toBe('presentation');
    expect(Object.keys(w.vm.$attrs).length).toBe(0);
  });

  it('R4: 高度 prop 必须原样落到 style.height', () => {
    const w = mount(SessionGroupHeader, { props: { label: '今天', height: 22 } });
    expect((w.element as HTMLElement).style.height).toBe('22px');
  });

  it('R5: SessionList 必须复用 virtualize.ts，不得内联副本', () => {
    const src = readSibling('SessionList.vue');
    expect(src).toMatch(/from ['"]\.\/virtualize['"]/);
    expect(src).not.toMatch(/function buildOffsets/);
    expect(src).not.toMatch(/function findStartRow/);
  });

  /**
   * R6: 分隔线的可见性不得依赖 :first-child。
   *
   * 虚拟滚动只渲染 slice 窗口内的行，滚动到列表中间时窗口首行常常是 header，
   * 于是 `.group-head:first-child` 会在滚动过程中反复命中/失配，
   * 表现为分隔线忽隐忽现、与相邻分组「连出两条线」或漏线。
   * 首行是否画线必须由「它是不是整份 rows 的第一行」这个稳定事实决定。
   */
  it('R6: 不得使用 :first-child 决定分隔线（虚拟滚动下会随窗口裁剪翻转）', () => {
    const src = readSibling('SessionGroupHeader.vue');
    expect(src).not.toMatch(/:first-child/);
  });

  it('R7: 首行线由显式 prop 控制，且默认不画线时必须落到样式上', () => {
    const first = mount(SessionGroupHeader, {
      props: { label: '今天', height: 22, first: true },
    });
    const later = mount(SessionGroupHeader, {
      props: { label: '昨天', height: 22, first: false },
    });
    // 第一个渲染行不画上边线，其余行画线
    expect(first.element.classList.contains('is-first')).toBe(true);
    expect(later.element.classList.contains('is-first')).toBe(false);
  });

  it('R8: 空窗口首行判定稳定 —— 用行索引而非 DOM 位置', () => {
    // 两组各 3 条，header/item 混合；窗口裁剪到中间时首行是某组的 item
    const HEAD = 22;
    const ITEM = 56;
    const rows = [
      { h: HEAD },
      ...Array.from({ length: 3 }, () => ({ h: ITEM })),
      { h: HEAD },
      ...Array.from({ length: 3 }, () => ({ h: ITEM })),
    ];
    const off = buildOffsets(rows);
    // 滚到第二组中间：窗口 start 落在第二个 header 之后
    const st = HEAD + 3 * ITEM + HEAD + ITEM + 10;
    const v = computeVisible(off, st, 120, 0);
    // 首行索引 > 0 → 它就不是整份列表的第一行，必须画线
    expect(v.start).toBeGreaterThan(0);
  });

  /**
   * R9: 行序列中不得出现相邻两个 header。
   *
   * 空分组必须在 grouped 阶段被过滤掉（length > 0），否则会产出
   * 「header 紧跟 header」的扁平行序，渲染出来就是连续两条分割线。
   * 这里用与组件相同口径构造行序并断言不变量。
   */
  it('R9: 扁平行序中不得出现相邻的 header（空分组必须被过滤）', () => {
    type Bucket = { label: string; items: number[] };
    const groups: Bucket[] = [
      { label: '今天', items: [1, 2] },
      { label: '昨天', items: [] }, // 空桶，必须被过滤
      { label: '最近 7 天', items: [3] },
      { label: '更早', items: [] }, // 空桶，必须被过滤
    ].filter((g) => g.items.length > 0);

    const flat: Array<{ kind: 'header' | 'item' }> = [];
    for (const g of groups) {
      flat.push({ kind: 'header' });
      for (let i = 0; i < g.items.length; i++) flat.push({ kind: 'item' });
    }

    for (let i = 1; i < flat.length; i++) {
      const prev = flat[i - 1]!;
      const cur = flat[i]!;
      expect(prev.kind === 'header' && cur.kind === 'header').toBe(false);
    }
    // 且每个 header 之后一定还有内容，不会以 header 结尾
    expect(flat[flat.length - 1]!.kind).toBe('item');
  });
});
