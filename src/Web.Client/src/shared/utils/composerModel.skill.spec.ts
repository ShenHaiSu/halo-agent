/** need22-F02：草稿模型 skill 段单测（frontend/02 §2.7 C1~C10）。 */
import { describe, expect, it } from 'vitest';
import {
  createEmptyDraft,
  deserialize,
  hasSkillSegment,
  isEmpty,
  removeSkillSegment,
  serialize,
  toSendPayload,
  type ComposerAttachment,
  type ComposerDraft,
} from '@shared/utils/composerModel';

function fakeAtt(attId: string): ComposerAttachment {
  return {
    attId,
    fileName: `${attId}.png`,
    mime: 'image/png',
    sizeBytes: 10,
    objectUrl: '',
    file: new File([], `${attId}.png`, { type: 'image/png' }),
    source: 'paste',
    status: 'ready',
  };
}

function draftOf(segments: ComposerDraft['segments'], attachments: ComposerDraft['attachments'] = {}): ComposerDraft {
  return { segments, attachments };
}

describe('composerModel skill 段（C1~C10）', () => {
  it('C1 text + skill 混合 serialize ⇒ "text[[skill:global/foo]]"', () => {
    const d = draftOf([
      { kind: 'text', text: '帮我改代码 ' },
      { kind: 'skill', scope: 'global', name: 'foo' },
    ]);
    expect(serialize(d)).toBe('帮我改代码 [[skill:global/foo]]');
  });

  it('C2 deserialize(serialize(draft)) ⇒ 段序列等价', () => {
    const d = draftOf([
      { kind: 'text', text: '前 ' },
      { kind: 'skill', scope: 'global', name: 'foo' },
      { kind: 'text', text: ' 后' },
    ]);
    const back = deserialize(serialize(d), {});
    expect(back.segments).toEqual([
      { kind: 'text', text: '前 ' },
      { kind: 'skill', scope: 'global', name: 'foo' },
      { kind: 'text', text: ' 后' },
    ]);
  });

  it('C3 deserialize 文本含 [image:att_x] 且附件存在 ⇒ 还原成 image 段（回归）', () => {
    const att = fakeAtt('att_x');
    const back = deserialize('看图 [image:att_x] 谢谢', { att_x: att });
    expect(back.segments).toEqual([
      { kind: 'text', text: '看图 ' },
      { kind: 'image', attId: 'att_x' },
      { kind: 'text', text: ' 谢谢' },
    ]);
  });

  it('C4 deserialize 文本含 [image:att_x] 但附件缺失 ⇒ 还原成文本（回归）', () => {
    const back = deserialize('看图 [image:att_x] 谢谢', {});
    expect(back.segments).toEqual([
      { kind: 'text', text: '看图 ' },
      { kind: 'text', text: '[image:att_x]' },
      { kind: 'text', text: ' 谢谢' },
    ]);
    expect(back.segments.some((s) => s.kind === 'image')).toBe(false);
  });

  it('C5 isEmpty：仅 skill 段 ⇒ false', () => {
    expect(isEmpty(draftOf([{ kind: 'skill', scope: 'global', name: 'foo' }]))).toBe(false);
  });

  it('C6 isEmpty：空草稿 ⇒ true（回归）', () => {
    expect(isEmpty(createEmptyDraft())).toBe(true);
    expect(isEmpty(draftOf([{ kind: 'text', text: '   ' }]))).toBe(true);
  });

  it('C7 toSendPayload：skill 段进 text、attachments 不受影响', async () => {
    const d = draftOf([{ kind: 'skill', scope: 'project', name: 'bar' }]);
    const payload = await toSendPayload(d);
    expect(payload.text).toBe('[[skill:project/bar]]');
    expect(payload.attachments.length).toBe(0);
  });

  it('C8 同名 skill 两个段 ⇒ serialize 只出一个 marker（D4 去重）', () => {
    const d = draftOf([
      { kind: 'skill', scope: 'global', name: 'foo' },
      { kind: 'text', text: ' ' },
      { kind: 'skill', scope: 'global', name: 'foo' },
    ]);
    const out = serialize(d);
    expect(out.split('[[skill:global/foo]]').length - 1).toBe(1);
  });

  it('C9 removeSkillSegment ⇒ 段被移除，其它段不动', () => {
    const d = draftOf([
      { kind: 'text', text: '前' },
      { kind: 'skill', scope: 'global', name: 'foo' },
      { kind: 'text', text: '后' },
    ]);
    const next = removeSkillSegment(d, 'global', 'foo');
    expect(next.segments).toEqual([
      { kind: 'text', text: '前' },
      { kind: 'text', text: '后' },
    ]);
    expect(hasSkillSegment(d, 'global', 'foo')).toBe(true);
    expect(hasSkillSegment(next, 'global', 'foo')).toBe(false);
  });

  it('C10 deserialize 尾部换行 ⇒ 不多出空行（回归）', () => {
    const back = deserialize('正文\n', {});
    expect(back.segments.length).toBe(1);
    expect(back.segments[0]).toEqual({ kind: 'text', text: '正文\n' });
  });
});
