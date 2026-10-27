// src/Web.Client/src/components/common/fuzzy.test.ts
// 命令面板检索算法测试（P5 A-8.1：12 + 5 用例）。
import { describe, expect, it } from 'vitest';
import { fuzzyScore, highlight } from './fuzzy';

describe('fuzzyScore：四档', () => {
  it('1. 空查询返回最低分', () => {
    expect(fuzzyScore('任何文字', '')).toEqual({ score: 1, indices: [] });
  });

  it('2. 完全相等 1000 分', () => {
    const s = fuzzyScore('设置', '设置');
    expect(s?.score).toBe(1000);
    expect(s?.indices).toEqual([0, 1]);
  });

  it('3. 相等忽略大小写', () => {
    expect(fuzzyScore('ABC', 'abc')?.score).toBe(1000);
  });

  it('4. 前缀 800 起', () => {
    const s = fuzzyScore('设置面板', '设置');
    expect(s?.score).toBeGreaterThanOrEqual(700);
    expect(s?.score).toBeLessThanOrEqual(800);
    expect(s?.indices).toEqual([0, 1]);
  });

  it('5. 前缀越短分越高', () => {
    const a = fuzzyScore('设置', '设')!;
    const b = fuzzyScore('设置面板很长很长', '设')!;
    expect(a.score).toBeGreaterThan(b.score);
  });

  it('6. 子串 500 起', () => {
    const s = fuzzyScore('打开设置页', '设置');
    expect(s?.score).toBeLessThanOrEqual(500);
    expect(s?.score).toBeGreaterThan(200);
  });

  it('7. 子串位置越靠前分越高', () => {
    const a = fuzzyScore('设置页', '设置')!;
    const b = fuzzyScore('打开设置页', '设置')!;
    expect(a.score).toBeGreaterThan(b.score);
  });

  it('8. 顺序匹配 fuzzy', () => {
    const s = fuzzyScore('新建会话', '新建');
    expect(s).not.toBeNull();
    expect(s!.score).toBeGreaterThanOrEqual(1);
  });

  it('9. 顺序不符返回 null', () => {
    expect(fuzzyScore('abc', 'zzz')).toBeNull();
  });

  it('10. 顺序缺字返回 null', () => {
    expect(fuzzyScore('ab', 'abc')).toBeNull();
  });

  it('11. 中文子串', () => {
    const s = fuzzyScore('切换权限模式', '权限');
    expect(s).not.toBeNull();
    expect(s?.indices).toEqual([2, 3]);
  });

  it('12. 英文大小写不敏感', () => {
    expect(fuzzyScore('New Session', 'new')).not.toBeNull();
    expect(fuzzyScore('New Session', 'NEW')).not.toBeNull();
  });
});

describe('highlight：片段', () => {
  it('13. 空命中整段不亮', () => {
    expect(highlight('设置页', [])).toEqual([{ text: '设置页', hit: false }]);
  });

  it('14. 连续命中合并一段', () => {
    expect(highlight('设置页', [0, 1])).toEqual([
      { text: '设置', hit: true },
      { text: '页', hit: false },
    ]);
  });

  it('15. 分散命中多段', () => {
    const segs = highlight('abcde', [0, 2, 4]);
    expect(segs.filter((s) => s.hit)).toHaveLength(3);
    expect(segs.map((s) => s.text).join('')).toBe('abcde');
  });

  it('16. 全命中一段', () => {
    expect(highlight('ab', [0, 1])).toEqual([{ text: 'ab', hit: true }]);
  });

  it('17. 空字符串', () => {
    expect(highlight('', [])).toEqual([{ text: '', hit: false }]);
  });
});
