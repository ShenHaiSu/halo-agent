/**
 * S7-E1 §5 验收 E1-1/E1-2：入口三段式预算（个数 → 单体 → 总量）+ 占位符同步清理。
 * 纯函数单测（validateAttachments），不碰 DOM / Store / Bridge。
 */
import { describe, expect, it } from 'vitest';
import {
  validateAttachments,
  MAX_FILES_PER_TURN,
  MAX_TOTAL_BYTES,
  MAX_IMAGE_BYTES,
  type SendAttachment,
} from '@shared/utils/composerModel';

function att(id: string, sizeBytes = 1_000): SendAttachment {
  return { attId: id, fileName: `${id}.png`, mime: 'image/png', base64: 'QQ==', sizeBytes };
}

/** n 张图 + 一一对应的占位符文本 */
function many(n: number, sizeBytes = 1_000): { text: string; list: SendAttachment[] } {
  const list: SendAttachment[] = [];
  let text = '';
  for (let i = 0; i < n; i++) {
    const id = `att_${String(i + 1).padStart(3, '0')}`;
    list.push(att(id, sizeBytes));
    text += `[image:${id}]`;
  }
  return { text, list };
}

describe('S7-E1 入口三段式预算（validateAttachments）', () => {
  it('E1-0 常量锁定：20 张 / 128MB / 单体 10MB（十进制）', () => {
    expect(MAX_FILES_PER_TURN).toBe(20);
    expect(MAX_TOTAL_BYTES).toBe(128_000_000);
    expect(MAX_IMAGE_BYTES).toBe(10_000_000);
  });

  it('E1-1：21 张 → 20 张通过、1 张被拒且占位符无悬空', () => {
    const { text, list } = many(21);
    const r = validateAttachments(text, list);
    expect(r.kept).toHaveLength(20);
    expect(r.dropped).toHaveLength(1);
    expect(r.dropped[0].attId).toBe('att_001');
    expect(r.dropped[0].reason).toBe('over-count');
    // 未被留下的那张，其占位符必须从文本里消失
    expect(r.text).not.toContain('[image:att_001]');
    // 其余 20 个占位符全在
    for (const a of r.kept) expect(r.text).toContain(`[image:${a.attId}]`);
    expect(r.text.match(/\[image:/g)).toHaveLength(20);
  });

  it('E1-2：单张 129MB（超总量）→ 被总量拒；单张 11MB（超单体）→ 被单体拒', () => {
    // 总量：一张 129MB 超过 128MB 总量
    const huge = validateAttachments('[image:att_big]', [att('att_big', 129_000_000)]);
    expect(huge.kept).toHaveLength(0);
    expect(huge.dropped[0].reason).toBe('over-single'); // 129MB 同时超单体，按顺序先命中单体
    expect(huge.text).toBe('');

    // 单独验证总量：两张各 9MB（各自不超单体），配额压到 12MB → 累计 18MB 超限，从最旧丢一张
    const twoBig = validateAttachments('[image:att_a][image:att_b]', [att('att_a', 9_000_000), att('att_b', 9_000_000)], {
      maxTotalBytes: 12_000_000,
    });
    expect(twoBig.kept.map((a) => a.attId)).toEqual(['att_b']);
    expect(twoBig.dropped[0]).toMatchObject({ attId: 'att_a', reason: 'over-total' });
    expect(twoBig.text).toBe('[image:att_b]');
  });

  it('E1-3：单体超 10MB 逐条丢，其余照发', () => {
    const r = validateAttachments('[image:att_ok][image:att_fat]', [att('att_ok', 5_000_000), att('att_fat', MAX_IMAGE_BYTES + 1)]);
    expect(r.kept.map((a) => a.attId)).toEqual(['att_ok']);
    expect(r.dropped[0]).toMatchObject({ attId: 'att_fat', reason: 'over-single' });
    expect(r.text).toBe('[image:att_ok]');
  });

  it('E1-4：同一 attId 出现两次 → 两处占位符都被清理（无悬空）', () => {
    const { list } = many(21);
    // 让第 1 张（会被个数丢掉）在文本里出现两次
    const text = '[image:att_001]中间[image:att_001]' + many(21).text.slice('[image:att_001]'.length);
    const r = validateAttachments(text, list);
    expect(r.dropped[0].attId).toBe('att_001');
    expect(r.text).not.toContain('[image:att_001]');
    expect(r.text.startsWith('中间')).toBe(true);
  });

  it('E1-5：未超限时零改动（文本与顺序完全不变）', () => {
    const text = '[image:att_a]看图[image:att_b]';
    const list = [att('att_a'), att('att_b')];
    const r = validateAttachments(text, list);
    expect(r.dropped).toHaveLength(0);
    expect(r.text).toBe(text);
    expect(r.kept.map((a) => a.attId)).toEqual(['att_a', 'att_b']);
  });

  it('E1-6：空清单/空文本零抖动', () => {
    expect(validateAttachments('', [])).toEqual({ kept: [], dropped: [], text: '' });
    expect(validateAttachments('纯文字', [])).toEqual({ kept: [], dropped: [], text: '纯文字' });
    // 图在但文本无占位符：不产生清理动作
    const r = validateAttachments('', [att('att_x')]);
    expect(r.kept).toHaveLength(1);
    expect(r.text).toBe('');
  });

  it('E1-7：可注入更严阈值（便于单测与将来产品调参）', () => {
    const r = validateAttachments('[image:a][image:b][image:c]', [att('a'), att('b'), att('c')], {
      maxFiles: 2,
      maxSingleBytes: 10_000_000,
      maxTotalBytes: 128_000_000,
    });
    expect(r.kept.map((x) => x.attId)).toEqual(['b', 'c']);
    expect(r.text).toBe('[image:b][image:c]');
  });

  it('E1-8：弃图顺序与三段式优先级一致（先个数，再单体，最后总量）', () => {
    // 21 张：第 1 张既超单体又是先被个数丢的 → 原因应为 over-count（先命中）
    const list: SendAttachment[] = [att('att_001', MAX_IMAGE_BYTES + 5)];
    let text = '[image:att_001]';
    for (let i = 2; i <= 21; i++) {
      const id = `att_${String(i).padStart(3, '0')}`;
      list.push(att(id));
      text += `[image:${id}]`;
    }
    const r = validateAttachments(text, list);
    expect(r.dropped).toHaveLength(1);
    expect(r.dropped[0]).toMatchObject({ attId: 'att_001', reason: 'over-count' });
  });
});
