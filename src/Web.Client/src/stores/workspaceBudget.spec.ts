/** 发送保留旧预算 + 首包用量本地补丁回归（业务语义命名）。 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { SessionMetadata, UnifiedChatMessage } from '@shared/bridge/hostBridge';

const { bridgeMock } = vi.hoisted(() => ({
  bridgeMock: {
    appendSessionMessage: vi.fn(),
    updateSessionTitle: vi.fn(),
    generateSessionTitle: vi.fn(),
    getContextBudget: vi.fn(),
    getSessionMessages: vi.fn(),
    getSessions: vi.fn(),
    healSessionHistory: vi.fn(),
    getLastActive: vi.fn(),
    setLastActive: vi.fn(),
    getTimelineSummary: vi.fn(),
  },
}));

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...actual, getHostBridge: () => bridgeMock };
});

import { useWorkspaceStore } from '@/stores/workspaceStore';

const WID = 'ws-budget-spec';
const SID = 'sess-budget-spec';
const OTHER_SID = 'sess-budget-other';

function msg(id: string): UnifiedChatMessage {
  return { id, role: 'user', content: 'hello', createdAtUtc: '2026-01-01T00:00:00Z' } as unknown as UnifiedChatMessage;
}

function baseBudget(): Record<string, unknown> {
  return {
    totalContextTokens: 128000,
    maxOutputTokens: 4096,
    safetyBuffer: 1024,
    usedInputTokens: 1000,
    availableInputTokens: 121880,
    usagePercentage: 0.8,
    isNearLimit: false,
    sessionHash: 'h1',
    protocol: 'chat',
    lastPromptTokens: 1000,
    lastCachedTokens: 200,
    cacheHitRate: 0.2,
    usedInputIsEstimated: false,
    occupancyBaseline: 'exact',
    occupancyState: 'known',
    billedPromptTokens: 0,
    billedCompletionTokens: 0,
  };
}

function seedBudget(budget: Record<string, unknown> | null): ReturnType<typeof useWorkspaceStore> {
  const ws = useWorkspaceStore();
  ws.activeWorkspaceId = WID;
  ws.activeSessionId = SID;
  ws.sessions = [
    { id: SID, title: 's1', messageCount: 1, createdAtUtc: '', updatedAtUtc: '' } as unknown as SessionMetadata,
    { id: OTHER_SID, title: 's2', messageCount: 0, createdAtUtc: '', updatedAtUtc: '' } as unknown as SessionMetadata,
  ];
  ws.sessionMessages = [msg('m1')];
  ws.timelineSummaries = [];
  ws.budgetStatus = (budget ? { ...budget } : null) as never;
  ws.budgetError = null;
  return ws;
}

beforeEach(() => {
  setActivePinia(createPinia());
  vi.clearAllMocks();
  bridgeMock.getSessionMessages.mockResolvedValue({ ok: true, data: [] });
  bridgeMock.getTimelineSummary.mockResolvedValue({ ok: true, data: [] });
  bridgeMock.getContextBudget.mockResolvedValue({ ok: true, data: null });
  bridgeMock.healSessionHistory.mockResolvedValue({ ok: true, data: { syntheticAppended: 0, orphanToolsRemoved: 0 } });
  bridgeMock.getSessions.mockResolvedValue({ ok: true, data: [] });
  bridgeMock.getLastActive.mockResolvedValue({ ok: true, data: { workspaceId: null, sessionId: null } });
  bridgeMock.setLastActive.mockResolvedValue({ ok: true, data: true });
});

describe('预算保留与本地补丁', () => {
  it('sendKeepsBudgetInsteadOfClearing', async () => {
    const ws = seedBudget(baseBudget());
    const before = ws.budgetStatus;
    await ws.loadSessionMessages(SID, { preserveBudget: true });
    expect(ws.budgetStatus).toBe(before);
    expect(ws.sessionMessages).toEqual([]);
  });

  it('reloadWithoutFlagClearsBudget', async () => {
    const ws = seedBudget(baseBudget());
    await ws.loadSessionMessages(SID);
    expect(ws.budgetStatus).toBeNull();
    expect(ws.sessionMessages).toEqual([]);
  });

  it('replayAndSwitchStillClearBudget', async () => {
    const ws = seedBudget(baseBudget());
    const before = ws.budgetStatus;
    await ws.selectSession(OTHER_SID);
    expect(ws.budgetStatus).not.toBe(before);
    expect(ws.budgetStatus).toBeNull();
  });

  it('usagePatchUpdatesBudgetWithoutBackend', () => {
    const ws = seedBudget(baseBudget());
    const before = ws.budgetStatus as unknown as Record<string, unknown>;
    ws.applyUsagePatch(SID, { inputTokens: 1500, cachedInputTokens: 300 });
    const after = ws.budgetStatus as unknown as Record<string, unknown>;
    expect(after).not.toBe(before);
    expect(after['usedInputTokens']).toBe(1500);
    expect(after['lastPromptTokens']).toBe(1500);
    expect(after['lastCachedTokens']).toBe(300);
    expect(after['cacheHitRate']).toBeCloseTo(0.2, 5);
    expect(after['occupancyBaseline']).toBe('exact');
    expect(after['occupancyState']).toBe('known');
    expect(after['usagePercentage']).toBe(1.2);
    expect(after['availableInputTokens']).toBe(128000 - 4096 - 1024 - 1500);
    expect(bridgeMock.getContextBudget).not.toHaveBeenCalled();
  });

  it('usagePatchIgnoresNonPositiveInput', () => {
    const ws = seedBudget(baseBudget());
    const before = ws.budgetStatus;
    ws.applyUsagePatch(SID, { inputTokens: 0 });
    expect(ws.budgetStatus).toBe(before);
    ws.applyUsagePatch(SID, { inputTokens: -5 });
    expect(ws.budgetStatus).toBe(before);
    ws.applyUsagePatch(SID, { inputTokens: Number.NaN });
    expect(ws.budgetStatus).toBe(before);
  });

  it('usagePatchSkipsCompactEstimate', () => {
    const ws = seedBudget({ ...baseBudget(), occupancyBaseline: 'compact-estimate' });
    const before = ws.budgetStatus;
    ws.applyUsagePatch(SID, { inputTokens: 2000, cachedInputTokens: 100 });
    expect(ws.budgetStatus).toBe(before);
  });

  it('usagePatchIgnoresBackgroundSession', () => {
    const ws = seedBudget(baseBudget());
    const before = ws.budgetStatus;
    ws.applyUsagePatch(OTHER_SID, { inputTokens: 2000 });
    expect(ws.budgetStatus).toBe(before);
  });

  it('usagePatchNeedsExistingBaseline', () => {
    const ws = seedBudget(null);
    ws.applyUsagePatch(SID, { inputTokens: 2000 });
    expect(ws.budgetStatus).toBeNull();
  });
});
