/** need22-F04：目录摘要 strip skill marker 单测（frontend/04 §7 H5）。 */
import { describe, expect, it } from 'vitest';
import { toDisplayText } from '@features/session/utils/historyToc';

describe('historyToc 摘要（H5）', () => {
  it('H5 目录摘要不含 [[skill:', () => {
    const text = toDisplayText('帮我改代码 [[skill:global/foo]] 谢谢');
    expect(text).not.toContain('[[skill:');
    expect(text).toContain('帮我改代码');
  });
});
