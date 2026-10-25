/** S4 验收 3：前端 oversize 阈值与后端同值（十进制 10M）。 */
import { describe, expect, it } from 'vitest';
import { MAX_IMAGE_BYTES } from '@shared/utils/composerModel';

describe('S4 single image limit (frontend mirror)', () => {
  it('MAX_IMAGE_BYTES 与后端 ImageAttachmentStore.MaxImageBytes 同值（十进制 10M）', () => {
    expect(MAX_IMAGE_BYTES).toBe(10_000_000);
  });

  it('边界语义：9MB 走 MAX_IMAGE_BYTES 比较为 ready，10M+1 为 oversize', () => {
    expect(9_000_000 > MAX_IMAGE_BYTES).toBe(false);
    expect(10_000_001 > MAX_IMAGE_BYTES).toBe(true);
  });
});
