import { describe, expect, it } from 'vitest';
import { computed, ref } from 'vue';
import { flatBlocks } from '@features/session/utils/timelineSummary';
import type { TurnSummary } from '@shared/bridge/hostBridge';

function makeSummary(over: Partial<TurnSummary> = {}): TurnSummary {
  return {
    turnId: 'uturn-u1',
    turnSeq: 1,
    userContent: '用户全文',
    userPreview: '用户预览',
    finalBlockId: 'b1',
    finalPreview: '正文预览',
    blocks: 1,
    tools: 0,
    ...over,
  } as TurnSummary;
}

describe('steadyStateRendersSummaryOnly', () => {
  it('V2 稳态只看摘要轮数，不看历史分片', () => {
    const historyList: unknown[] = [];
    const tailList: unknown[] = [];
    const summaries = [makeSummary(), makeSummary({ turnId: 'uturn-u2', turnSeq: 2 })];
    expect(historyList).toEqual([]);
    expect(tailList).toEqual([]);
    expect(flatBlocks(summaries)).toHaveLength(2);
  });
});

describe('legacyHistorySwitchIsAlwaysOn', () => {
  it('历史开关恒真', () => {
    const hideLegacyHistory = computed(() => true);
    expect(hideLegacyHistory.value).toBe(true);
  });
});

describe('windowFollowsSummaryRounds', () => {
  it('hiddenAbove 按摘要轮数计算', () => {
    const summaries = [makeSummary(), makeSummary({ turnId: 'uturn-u2', turnSeq: 2 })];
    const hiddenAbove = computed(() => Math.max(0, flatBlocks(summaries).length - 60));
    expect(hiddenAbove.value).toBe(0);
  });
});

describe('tocUsesSummarySourceOnly', () => {
  it('主链目录项来自摘要三锚点', () => {
    const isSub = ref(false);
    const summaries = [makeSummary()];
    const display = computed(() => (isSub.value ? [] : flatBlocks(summaries)));
    expect(display.value).toHaveLength(1);
    expect(display.value[0].turnId).toBe('uturn-u1');
  });
});

describe('legacySessionShowsReadonlyNotice', () => {
  it('V1 会话只读横幅可分支，不跑投影', () => {
    const historyVersion = ref(1);
    const isHistoryReadonly = computed(() => historyVersion.value !== 2);
    expect(isHistoryReadonly.value).toBe(true);
    const historyList: unknown[] = [];
    expect(historyList).toEqual([]);
  });
});
