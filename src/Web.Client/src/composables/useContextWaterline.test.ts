// P23：水位线 composable + session 快照/事件冲突冻结。
import { beforeEach, describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { ref } from 'vue';
import { useContextWaterline } from './useContextWaterline';
import { useSessionStore } from '@/stores/session';

function wl(last: number | null, est: number | null, win: number | null) {
  return useContextWaterline({ lastPromptTokens: ref(last), estimatedInitialTokens: ref(est), contextWindow: ref(win) });
}

describe('useContextWaterline', () => {
  it('无窗口隐藏（不制造 0% 实测态）', () => {
    const m = wl(9216, null, null);
    expect(m.visible.value).toBe(false);
    expect(m.state.value).toBe('hidden');
    expect(m.pct.value).toBeNull();
  });

  it('used==null 隐藏', () => {
    const m = wl(null, null, 128000);
    expect(m.visible.value).toBe(false);
    expect(m.state.value).toBe('hidden');
  });

  it('零 prompt 显示估算（不回退实测）', () => {
    const m = wl(null, 384, 128000);
    expect(m.visible.value).toBe(true);
    expect(m.state.value).toBe('estimated');
    expect(m.isEstimated.value).toBe(true);
    expect(m.label.value).toBe('约0%·预估');
  });

  it('实测态百分比与 hover 文本', () => {
    const m = wl(9216, 384, 128000);
    expect(m.state.value).toBe('measured');
    expect(m.pct.value).toBe(7);
    expect(m.label.value).toBe('7%');
    expect(m.hoverText.value).toBe('9,216 / 128,000');
    expect(m.isEstimated.value).toBe(false);
  });

  it('百分比取整 round', () => {
    const m = wl(1000, null, 3000);
    expect(m.pct.value).toBe(33);
  });

  it('阈值颜色：<70 常规 / 70-89 警告 / >=90 危险', () => {
    expect(wl(69, null, 100).tone.value).toBe('normal');
    expect(wl(70, null, 100).tone.value).toBe('warn');
    expect(wl(89, null, 100).tone.value).toBe('warn');
    expect(wl(90, null, 100).tone.value).toBe('danger');
  });

  it('超窗钳制 100% + 红色 + /compact 提示', () => {
    const m = wl(200000, null, 128000);
    expect(m.state.value).toBe('overflow');
    expect(m.pct.value).toBe(100);
    expect(m.barPct.value).toBe(100);
    expect(m.tone.value).toBe('danger');
    expect(m.overflowHint.value).toContain('/compact');
  });

  it('负值钳制 >=0', () => {
    const m = wl(-5, null, 128000);
    expect(m.used.value).toBe(0);
    expect(m.pct.value).toBe(0);
  });
});

describe('session 水位线快照/事件冲突冻结', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('老快照缺 llmCalls 按 0 兼容', () => {
    const s = useSessionStore();
    s.applyInfo({ usage: { inputTotal: 10, outputTotal: 20 } });
    expect(s.usage.input).toBe(10);
    expect(s.llmCalls).toBe(0);
    expect(s.lastPromptTokens).toBeNull();
  });

  it('新字段读取 + 事件总值覆盖', () => {
    const s = useSessionStore();
    s.applySnapshotWaterline(
      { lastPromptTokens: 100, lastCachedTokens: 10, estimatedInitialTokens: 50, sessionLlmCalls: 2 },
      's-1',
    );
    expect(s.lastPromptTokens).toBe(100);
    expect(s.llmCalls).toBe(2);
    s.applyUsageEvent({ prompt: 200, cached: 20, llmCalls: 3 });
    expect(s.lastPromptTokens).toBe(200);
    expect(s.lastCachedTokens).toBe(20);
    expect(s.llmCalls).toBe(3);
  });

  it('同会话快照晚到不回退事件值', () => {
    const s = useSessionStore();
    s.applySnapshotWaterline({ lastPromptTokens: 100, sessionLlmCalls: 1 }, 's-1');
    s.applyUsageEvent({ prompt: 500, llmCalls: 5 });
    // 晚到的旧快照（同会话）不得回退
    s.applySnapshotWaterline({ lastPromptTokens: 100, sessionLlmCalls: 1 }, 's-1');
    expect(s.lastPromptTokens).toBe(500);
    expect(s.llmCalls).toBe(5);
  });

  it('切会话全量采纳（含归零）', () => {
    const s = useSessionStore();
    s.applySnapshotWaterline({ lastPromptTokens: 500, sessionLlmCalls: 5 }, 's-1');
    s.resetWaterline();
    expect(s.lastPromptTokens).toBeNull();
    expect(s.llmCalls).toBe(0);
    s.applySnapshotWaterline({ lastPromptTokens: null, estimatedInitialTokens: 60, sessionLlmCalls: 0 }, 's-2');
    expect(s.lastPromptTokens).toBeNull();
    expect(s.estimatedInitialTokens).toBe(60);
    expect(s.llmCalls).toBe(0);
  });
});

describe('eventBridge usage 事件同步水位', () => {
  it('usage 事件同时更新 lastPrompt/llmCalls + stream 保留', async () => {
    const { createPinia: cp, setActivePinia: sap } = await import('pinia');
    sap(cp());
    const { useMessagesStore } = await import('@/stores/messages');
    const { useSessionStore: uss } = await import('@/stores/session');
    const { useStreamStore: ust } = await import('@/stores/stream');
    const { dispatchFrames: df } = await import('@/stores/eventBridge');
    const messages = useMessagesStore();
    const session = uss();
    const stream = ust();
    messages.setLastSeq(0);
    df([{ t: 'evt', seq: 1, k: 'usage', p: { prompt: 300, completion: 30, cached: 40, llmCalls: 4 } }] as never);
    expect(session.lastPromptTokens).toBe(300);
    expect(session.lastCachedTokens).toBe(40);
    expect(session.llmCalls).toBe(4);
    expect(stream.usage.lastPromptTokens).toBe(300);
    expect(messages.lastSeq).toBe(1);
  });
});
