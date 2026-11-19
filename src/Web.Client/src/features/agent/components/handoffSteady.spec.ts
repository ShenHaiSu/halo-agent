/** 流稳交接：数据未齐时保持进行态，齐备后才切稳态；空内容不出折叠行。 */
import { beforeEach, describe, expect, it } from 'vitest';
import { mount, type VueWrapper } from '@vue/test-utils';
import naive from 'naive-ui';
import { nextTick, reactive } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import UserTurnBlock from '@features/agent/components/UserTurnBlock.vue';
import UserTurnTransition from '@features/agent/components/UserTurnTransition.vue';
import { TimelineViewKey, timelineViewFallback } from '@features/agent/composables/timelineViewKey';
import type { UserTurn, UserTurnStep } from '@features/session/utils/userTurn';

type StepSeed = { content?: string; reasoning?: string; toolMessages?: unknown[] };

function makeTurn(stepsSeed: StepSeed[], isRunning: boolean): UserTurn {
  const steps = stepsSeed.map(
    (s, i): UserTurnStep =>
      ({
        iteration: i + 1,
        assistantId: `a${i + 1}`,
        content: s.content ?? '',
        reasoning: s.reasoning ?? null,
        toolMessages: (s.toolMessages ?? []) as never,
        toolCalls: [],
        telemetry: {} as never,
        createdAtUtc: '',
      }) as UserTurnStep
  );
  return {
    key: 'uturn-u1',
    userId: 'u1',
    userMsg: { id: 'u1', role: 'user', content: '问题' } as never,
    pending: false,
    requestId: null,
    steps,
    stepCount: steps.length,
    toolCount: 0,
    failedCount: 0,
    contentChars: steps.reduce((n, st) => n + String(st.content || '').length, 0),
    reasoningChars: 0,
    startedAtUtc: '',
    endedAtUtc: '',
    isRunning,
    phase: isRunning ? 'running' : 'success',
    phaseLabel: '',
    summary: '',
    fullSummary: '',
  } as unknown as UserTurn;
}

function mountBlock(turn: UserTurn, opts: { liveCompleting?: boolean; steadyReady?: boolean } = {}) {
  const vm = reactive({ ...timelineViewFallback(), steadyReady: opts.steadyReady ?? false, readingWindow: false });
  const wrapper = mount(UserTurnBlock, {
    props: {
      turn,
      expanded: true,
      liveSegs: [{ kind: 'text', key: 'seg-1', text: '进行中', card: null }],
      isGenerating: true,
      liveCompleting: opts.liveCompleting ?? false,
      handoffAnim: true,
    },
    global: {
      plugins: [naive, createPinia()],
      provide: { [TimelineViewKey as unknown as string]: vm },
      stubs: { ToolCallCard: { template: '<div class="tool-card-stub" />' } },
    },
  });
  return { wrapper, vm };
}

beforeEach(() => {
  setActivePinia(createPinia());
});

describe('流稳交接稳态准入', () => {
  it('Handoff_WithUnreadyData_KeepsLiveSummary', async () => {
    const turn = makeTurn([{ content: '第一段' }, { content: '结论' }], true);
    const { wrapper, vm } = mountBlock(turn, { liveCompleting: true, steadyReady: false });
    expect(wrapper.find('[data-testid="user-turn-live"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="user-turn-summary"]').exists()).toBe(false);
    vm.steadyReady = true;
    await nextTick();
    expect(wrapper.find('[data-testid="user-turn-summary"]').exists()).toBe(true);
    expect(wrapper.html()).toContain('结论');
  });

  it('HistoricalTurn_SteadyIgnoresReadyFlag', () => {
    const turn = makeTurn([{ content: '第一段' }, { content: '结论' }], false);
    const { wrapper } = mountBlock(turn, { steadyReady: false });
    expect(wrapper.find('[data-testid="user-turn-summary"]').exists()).toBe(true);
    expect(wrapper.html()).toContain('结论');
  });

  it('EmptySteps_RendersNoSummaryRow', () => {
    const turn = makeTurn([{ content: '' }, { content: '' }], false);
    const { wrapper } = mountBlock(turn, { steadyReady: true });
    expect(wrapper.find('[data-testid="user-turn-summary"]').exists()).toBe(false);
  });

  it('Handoff_WholeSequence_NoEmptySteady', async () => {
    const turn = makeTurn([{ content: '第一段' }, { content: '结论' }], true);
    const { wrapper, vm } = mountBlock(turn, { liveCompleting: true, steadyReady: false });
    // 对账未回：进行态可见，无空折叠行。
    expect(wrapper.find('[data-testid="user-turn-live"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="user-turn-summary"]').exists()).toBe(false);
    // 对账回：稳态内容上屏。
    vm.steadyReady = true;
    await nextTick();
    expect(wrapper.find('[data-testid="user-turn-summary"]').exists()).toBe(true);
    expect(wrapper.html()).toContain('结论');
    // 对账回灌后投影重建，末轮 isRunning 落为 false；交接窗关闭 → 无残留 live。
    await wrapper.setProps({ liveCompleting: false, turn: { ...turn, isRunning: false } });
    expect(wrapper.find('[data-testid="user-turn-live"]').exists()).toBe(false);
  });
});

describe('交接容器 hold-live', () => {
  function mountTransition(forceLive: boolean): VueWrapper {
    return mount(UserTurnTransition, {
      props: { running: false, completing: true, animOn: true, forceLive },
      slots: {
        live: '<div data-testid="slot-live">LIVE</div>',
        default: '<div data-testid="slot-steady">STEADY</div>',
      },
    });
  }

  it('ForceLive_KeepsLiveWithoutFade', () => {
    const w = mountTransition(true);
    expect(w.find('[data-testid="user-turn-handoff-live"]').exists()).toBe(true);
    expect(w.find('[data-testid="slot-live"]').exists()).toBe(true);
    expect(w.find('.uturn-live-fading').exists()).toBe(false);
  });
});
