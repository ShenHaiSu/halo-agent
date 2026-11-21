import { describe, expect, it } from 'vitest';

/** C4 删除完成判据：V2 compact 桩保留（与 V1 compactStub 是两套东西，误删即红）。 */
describe('noLegacyProjectionSymbolsRemain', () => {
  it('V2 compact 桩仍在（防误删）', async () => {
    const mod = await import('@features/session/utils/timelineSummary');
    expect(typeof mod.flatBlocks).toBe('function');
    expect(typeof mod.summaryAnchorKeys).toBe('function');
  });
});
