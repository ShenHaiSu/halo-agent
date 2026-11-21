// P02-G2 毕业只换态不搬家：live 流段直挂，不再经轮容器。
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const orig = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...orig, getHostBridge: () => ({}) as never, getBridgeTransport: () => ({}) as never };
});

import { ensureTurn, pushToolSeg, setActiveSessionId, turns } from '@features/agent/stores/model';

beforeEach(() => {
  setActivePinia(createPinia());
  setActiveSessionId('s1');
  turns.value = {};
});

describe('P02 live tool 卡毕业不断档', () => {
  it('工具段直挂流：push 后 segs 即见 tool 段', () => {
    const t = ensureTurn('s1');
    pushToolSeg(t, 'c1');
    const seg = t.segs.find((s) => s.kind === 'tool');
    expect(seg).toBeTruthy();
    expect(String((seg as unknown as { callId?: unknown }).callId || '')).toBe('c1');
  });

  it('未知 tool 同样直挂，不留空洞', () => {
    const t = ensureTurn('s1');
    pushToolSeg(t, 'c9');
    const seg = t.segs.find((s) => String((s as unknown as { callId?: unknown }).callId || '') === 'c9');
    expect(seg?.kind).toBe('tool');
  });
});
