/**
 * S6 §4.3：转录图片顺序与发包一致（orderImagesByContent / orderedImagesOf）。
 * 注：放在 session/utils 侧，因为被测函数属于 feature-session；
 * §4.4 的错误码透传用例在 agent 侧（agent/stores/need23-toolPersisted.spec.ts）。
 */
import { describe, expect, it } from 'vitest';
import type { ChatImageRef, UnifiedChatMessage } from '@shared/bridge/hostBridge';
import { orderImagesByContent, orderedImagesOf } from '@features/session/utils/turnGroup';

function img(id: string): ChatImageRef {
  return {
    imageId: id,
    fileName: `${id}.png`,
    mime: 'image/png',
    relPath: `attachments/20260101/${id}_${id}.png`,
    width: 1,
    height: 1,
    sizeBytes: 68,
    sha16: id,
  };
}

const ids = (list: ChatImageRef[]): string[] => list.map((i) => i.imageId);

describe('S6 位置契约：转录图片顺序（orderImagesByContent）', () => {
  it('占位符顺序覆盖数组顺序（两张图中间夹文字→按文本位置排）', () => {
    const ordered = orderImagesByContent('[image:img1]中[image:img2]后', [img('img2'), img('img1')]);
    expect(ids(ordered)).toEqual(['img1', 'img2']);
  });

  it('无占位符 ⇒ 保持数组序（老历史零抖动）', () => {
    const ordered = orderImagesByContent('纯文字[图片：a.png]', [img('img_b'), img('img_a')]);
    expect(ids(ordered)).toEqual(['img_b', 'img_a']);
  });

  it('不在文本中的图沉底且保留相对序', () => {
    const ordered = orderImagesByContent('[image:img2]文字', [img('orphan1'), img('img2'), img('orphan2')]);
    expect(ids(ordered)).toEqual(['img2', 'orphan1', 'orphan2']);
  });

  it('空文本/单图/空列表不炸', () => {
    expect(orderImagesByContent(null, [img('a')])).toEqual([img('a')]);
    expect(orderImagesByContent('', [img('a'), img('b')]).map((i) => i.imageId)).toEqual(['a', 'b']);
    expect(orderImagesByContent('[image:a]', [])).toEqual([]);
    expect(orderImagesByContent('[image:a]', null)).toEqual([]);
  });

  it('orderedImagesOf 读取消息正文做排序', () => {
    const msg = {
      id: 'm1',
      role: 'user',
      content: '[image:img2]看这两张[image:img1]',
      images: [img('img1'), img('img2')],
    } as unknown as UnifiedChatMessage;
    expect(ids(orderedImagesOf(msg))).toEqual(['img2', 'img1']);
  });

  it('重复占位符按首次出现位置排序', () => {
    const ordered = orderImagesByContent('[image:img2][image:img1][image:img2]', [img('img1'), img('img2')]);
    expect(ids(ordered)).toEqual(['img2', 'img1']);
  });

  it('空消息/无图零抖动', () => {
    expect(orderedImagesOf(null)).toEqual([]);
    expect(orderedImagesOf({ id: 'x', role: 'user', content: 'a' } as unknown as UnifiedChatMessage)).toEqual([]);
  });
});
