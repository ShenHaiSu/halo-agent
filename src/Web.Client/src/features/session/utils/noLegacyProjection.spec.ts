import { describe, expect, it } from 'vitest';

/** 删除完成判据：摘要紧凑轮透传保留（与旧压缩桩是两套东西，误删即红）。 */
describe('noLegacyProjectionSymbolsRemain', () => {
  it('摘要紧凑轮透传仍在（防误删）', async () => {
    const mod = await import('@features/session/utils/timelineSummary');
    expect(typeof mod.flatBlocks).toBe('function');
    expect(typeof mod.summaryAnchorKeys).toBe('function');
  });
});
