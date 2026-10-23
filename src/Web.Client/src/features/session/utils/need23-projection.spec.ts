/** need23-R2：转录投影内容指纹缓存 + 增量复用单测。 */
import { describe, expect, it } from 'vitest';
import { projectTranscript, incremental, checkGeneration } from '@features/session/utils/transcriptProjection';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';

function msg(id: string, role: 'user' | 'assistant' | 'tool', content = ''): UnifiedChatMessage {
  return { id, role, content, createdAtUtc: '' } as UnifiedChatMessage;
}

describe('projectTranscript（need23-R2 指纹缓存）', () => {
  it('同数组引用 + 内容未变 → 返回同一 projection 实例', () => {
    const list = [msg('u1', 'user', 'hi'), msg('a1', 'assistant', 'yo')];
    const p1 = projectTranscript(list);
    const p2 = projectTranscript(list);
    expect(p2).toBe(p1);
  });

  it('新数组引用但内容指纹一致（浅拷贝）→ 仍命中同一 projection', () => {
    const list = [msg('u1', 'user', 'hi'), msg('a1', 'assistant', 'yo')];
    const p1 = projectTranscript(list);
    const copy = [...list];
    const p2 = projectTranscript(copy);
    expect(p2).toBe(p1);
  });

  it('尾部追加一条 → 指纹变化，重建且 generation 递增', () => {
    const list = [msg('u1', 'user', 'hi'), msg('a1', 'assistant', 'yo')];
    const p1 = projectTranscript(list);
    const grown = [...list, msg('t1', 'tool', 'out')];
    const p2 = projectTranscript(grown);
    expect(p2).not.toBe(p1);
    expect(p2.generation).toBeGreaterThan(p1.generation);
    expect(p2.history.length + p2.tail.length).toBe(3);
  });

  it('同长度但尾 id 变化（截断重放）→ 不误命中', () => {
    const list = [msg('u1', 'user', 'hi'), msg('a1', 'assistant', 'yo')];
    const p1 = projectTranscript(list);
    const replaced = [list[0], msg('a2', 'assistant', 'yo2')];
    const p2 = projectTranscript(replaced);
    expect(p2).not.toBe(p1);
    expect(p2.tail[p2.tail.length - 1]?.key).toBe('t-a2');
  });

  it('tail 默认 8 条，history 为其前缀', () => {
    const list = Array.from({ length: 12 }, (_, i) => msg(`m${i}`, i % 2 ? 'assistant' : 'user'));
    const p = projectTranscript(list);
    expect(p.tail).toHaveLength(8);
    expect(p.history).toHaveLength(4);
  });

  it('checkGeneration 对过期 generation 返回 false', () => {
    const list = [msg('u1', 'user', 'hi')];
    const p1 = projectTranscript(list);
    projectTranscript([...list, msg('a1', 'assistant', 'x')]);
    expect(checkGeneration(p1, p1.generation)).toBe(true);
    expect(checkGeneration(p1, p1.generation + 1)).toBe(false);
  });

  it('incremental：前缀不变时复用 history 条目引用', () => {
    const base = Array.from({ length: 10 }, (_, i) => msg(`m${i}`, i % 2 ? 'assistant' : 'user'));
    const prev = projectTranscript(base);
    const next = [...base, msg('m10', 'user', 'tail')];
    const p2 = incremental(prev, base, next);
    // 前缀未变 → 前若干条 history 引用相同
    const reuse = Math.min(prev.history.length, p2.history.length);
    if (reuse > 0) expect(p2.history[0]).toBe(prev.history[0]);
  });
});
