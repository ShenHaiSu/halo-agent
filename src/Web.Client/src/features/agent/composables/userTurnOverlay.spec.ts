// need02-P2：乐观 overlay → UserTurn 投影贯通（G1 发送即现的回归锁）。
// 不 mock 后端回包：仅设置 turn.pendingUser，断言 useTimeline().timelineItems 立即可见 user-turn。
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const orig = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...orig, getHostBridge: () => ({}) as never, getBridgeTransport: () => ({}) as never };
});

import { useTimeline } from '@features/agent/composables/useTimeline';
import { setActiveSessionId, setPendingUser } from '@features/agent/stores/model';
import { makePendingUser } from '@features/session/utils/pendingUser';
import { useWorkspaceStore } from '@/stores/workspaceStore';

beforeEach(() => {
  setActivePinia(createPinia());
  try {
    window.localStorage.removeItem('halo-need02-p1');
    window.localStorage.removeItem('halo-need02-p2');
  } catch {
    /* ignore */
  }
  setActiveSessionId('s1');
});

describe('need02-P2 overlay 贯通', () => {
  it('pending 插入后 timelineItems 立即可见 user-turn（不等回包）', () => {
    const ws = useWorkspaceStore();
    ws.sessionMessages = [];
    setPendingUser('s1', makePendingUser({ requestId: 'req1', sessionId: 's1', content: 'hi' }));
    // activeSessionId 需与 pending 同会话，overlay 才生效
    try {
      (ws as unknown as { activeSessionId: string }).activeSessionId = 's1';
    } catch {
      /* ignore */
    }
    const t = useTimeline();
    const items = t.timelineItems.value;
    const turns = items.filter((e) => e.kind === 'user-turn');
    expect(turns).toHaveLength(1);
    const turn = (turns[0] as unknown as { turn: { pending: boolean; key: string } }).turn;
    expect(turn.pending).toBe(true);
    expect(turn.key).toBe('uturn-pending-req1');
  });
});
