import { describe, expect, it } from 'vitest';
import { MockTransport } from './MockTransport';

describe('MockTransport.cancel 收尾帧', () => {
  it('R4: agent.cancel 必须发 turn.finished{byUser:true}', async () => {
    const t = new MockTransport();
    await t.invoke('agent.prompt', { text: '你好' });
    await t.invoke('agent.cancel', {});

    const { frames } = await t.pollEvents(0);
    const finished = frames.filter((f) => f.k === 'turn.finished');
    expect(finished.length).toBeGreaterThan(0);
    const payload = finished[finished.length - 1]!.p as Record<string, unknown>;
    expect(payload.byUser).toBe(true);
    expect(payload.truncated).toBe(true);
    t.dispose();
  });
});
