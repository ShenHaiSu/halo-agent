import { describe, expect, it } from 'vitest';
import { compactTurnLabel, isCompactTurn } from './compactTurn';

describe('compactTurn', () => {
  it('只认结构化 kind', () => {
    expect(isCompactTurn({ kind: 'compact' })).toBe(true);
    expect(isCompactTurn({ kind: 'turn' })).toBe(false);
    expect(isCompactTurn({ kind: undefined })).toBe(false);
    expect(isCompactTurn({ kind: 'unknown' })).toBe(false);
  });

  it('标签使用真实退役条数', () => {
    expect(compactTurnLabel(42)).toBe('上下文压缩 · 42 条 → 摘要');
    expect(compactTurnLabel(0)).toBe('上下文压缩');
    expect(compactTurnLabel(Number.NaN)).toBe('上下文压缩');
  });
});
