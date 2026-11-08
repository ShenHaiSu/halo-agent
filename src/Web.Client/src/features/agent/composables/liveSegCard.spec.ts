// P02-G2 毕业只换态不搬家：live 索引摘牌后，轮内 tool 段不断档（终态卡原位保留，未知段占位）。
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const orig = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...orig, getHostBridge: () => ({}) as never, getBridgeTransport: () => ({}) as never };
});

import { useTimeline } from '@features/agent/composables/useTimeline';
import { ensureTurn, pushToolSeg, setActiveSessionId, turns } from '@features/agent/stores/model';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';

beforeEach(() => {
  setActivePinia(createPinia());
  try {
    window.localStorage.removeItem('halo-need02-p1');
    window.localStorage.removeItem('halo-need02-p2');
    window.localStorage.removeItem('halo-need02-p3');
    window.localStorage.removeItem('halo-need02-p4');
  } catch {
    /* ignore */
  }
  setActiveSessionId('s1');
  turns.value = {};
});

function toolMsg(id: string, callId: string, content: string): UnifiedChatMessage {
  return {
    id,
    role: 'tool',
    toolCallId: callId,
    name: 'read_file',
    content,
    createdAtUtc: '2026-10-26T11:59:20Z',
  } as unknown as UnifiedChatMessage;
}

describe('P02 live tool 卡毕业不断档', () => {
  it('已落盘 tool（live 索引无卡）→ 合成终态 done 卡，resultText 保留', () => {
    const ws = useWorkspaceStore();
    ws.sessionMessages = [toolMsg('t1', 'c1', 'out1')] as never;
    try {
      (ws as unknown as { activeSessionId: string }).activeSessionId = 's1';
    } catch {
      /* ignore */
    }
    const t = ensureTurn('s1');
    pushToolSeg(t, 'c1');
    const tl = useTimeline();
    const segs = tl.liveInclusiveSegs.value;
    expect(segs.map((s) => s.key)).toContain(t.segs[t.segs.length - 1].key);
    const seg = segs.find((s) => s.kind === 'tool');
    expect(seg).toBeTruthy();
    expect(seg?.card?.callId).toBe('c1');
    expect(seg?.card?.status).toBe('done');
    expect(seg?.card?.resultText).toBe('out1');
  });

  it('未知 tool（卡未建、包未落）→ running 占位卡，不留空洞', () => {
    const ws = useWorkspaceStore();
    ws.sessionMessages = [] as never;
    try {
      (ws as unknown as { activeSessionId: string }).activeSessionId = 's1';
    } catch {
      /* ignore */
    }
    const t = ensureTurn('s1');
    pushToolSeg(t, 'c9');
    const tl = useTimeline();
    const seg = tl.liveInclusiveSegs.value.find((s) => s.kind === 'tool');
    expect(seg?.card?.callId).toBe('c9');
    expect(seg?.card?.status).toBe('running');
  });
});
