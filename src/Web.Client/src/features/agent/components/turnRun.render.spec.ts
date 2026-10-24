/**
 * need-01 P2：无正文轮次合并的渲染层验证（docs/need-01/06 §6.2 的 5 条断言）。
 *
 * 挂载方式对齐 timelineVisual.spec.ts 的 mountTimeline：真 mount TimelineView + naive 插件，
 * 断言的是「用户真正看到的东西」——舱的数量、汇总行文本、红点形态、明细节数。
 * run 的聚合语义由 turnRun.spec.ts 覆盖，这里只关心渲染契约。
 */
import { describe, expect, it, beforeEach } from 'vitest';
import { mount } from '@vue/test-utils';
import naive from 'naive-ui';
import { createPinia, setActivePinia } from 'pinia';
import TimelineView from '@features/agent/components/TimelineView.vue';
import TurnRunItem from '@features/agent/components/TurnRunItem.vue';
import { buildTimelineEntries, buildTurnGroups, type TimelineEntry, type TurnGroup } from '@features/session/utils/turnGroup';
import { buildTurnRuns, makeTurnRun } from '@features/session/utils/turnRun';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';

type Extra = Record<string, unknown>;
const msg = (id: string, role: string, content: string | null, extra: Extra = {}): UnifiedChatMessage =>
  ({ id, role, content, createdAtUtc: '2026-10-24T00:00:00Z', ...extra }) as unknown as UnifiedChatMessage;

/** 一步无正文迭代：assistant(content=null) + toolCount 个 tool 回包（前 failTools 个带 [Tool Error）。 */
function ghostStep(idx: number, toolCount = 1, failTools = 0, extra: Extra = {}): UnifiedChatMessage[] {
  const callIds = Array.from({ length: toolCount }, (_, i) => `c${idx}-${i}`);
  return [
    msg(`a${idx}`, 'assistant', null, {
      toolCalls: callIds.map((c) => ({ id: c, type: 'function', function: { name: 'tool', arguments: '{}' } })),
      ...extra,
    }),
    ...callIds.map((c, i) => msg(`t${idx}-${i}`, 'tool', i < failTools ? '[Tool Error js] boom' : 'ok', { toolCallId: c })),
  ];
}

/** 8 个无正文 iteration，工具总数/失败数可控，返回原始 entries（未聚合）。 */
function ghostEntries(steps = 8, toolsPerStep = 1, failToolsPerStep = 0): TimelineEntry[] {
  const msgs: UnifiedChatMessage[] = [msg('u1', 'user', '跑一下')];
  for (let i = 1; i <= steps; i++) msgs.push(...ghostStep(i, toolsPerStep, failToolsPerStep));
  return buildTimelineEntries(msgs, buildTurnGroups(msgs));
}

function mountTimeline(items: TimelineEntry[], opts: { isRunExpanded?: (k: string) => boolean } = {}) {
  return mount(TimelineView, {
    props: {
      isEmpty: false,
      items,
      historyItems: items,
      tailItems: [],
      chatWidthMode: 'centered',
      isExpanded: () => false,
      isRunExpanded: opts.isRunExpanded,
      anchorRef: () => () => undefined,
      msgImages: () => [],
      thumbOf: () => '',
      hasLost: () => false,
      renderHtml: (t: string) => t,
      renderStream: (t: string) => t,
      cardFor: (m: UnifiedChatMessage) => ({ callId: String(m.id), functionName: 'tool' }) as never,
      toolKey: (m: UnifiedChatMessage) => String(m.id),
      hasFailed: (g: TurnGroup) => (g.toolMessages || []).some((m) => String(m.content || '').includes('[Tool Error')),
      failedCount: (g: TurnGroup) => (g.toolMessages || []).filter((m) => String(m.content || '').includes('[Tool Error')).length,
      showTelemetry: () => false,
      protocolOf: () => '',
      cacheLineOf: () => '',
      hasActiveTurn: false,
      thinkingLine: '',
      isGenerating: false,
      segs: [],
      segKillSwitch: false,
      fallbackCards: [],
      streamingText: '',
      showThinking: false,
      turnNotice: '',
      lastError: '',
      showRetry: false,
      liveTelemetry: null,
      menuVisible: false,
      menuX: 0,
      menuY: 0,
      menuBusy: false,
      menuCanCopy: false,
    },
    global: { plugins: [naive] },
  });
}

const cardFor = (m: UnifiedChatMessage) => ({ callId: String(m.id), functionName: 'tool' }) as never;
const toolKey = (m: UnifiedChatMessage) => String(m.id);

// ToolCallCard 在 setup 里直连 workspaceStore，需要活动 pinia 实例。
beforeEach(() => {
  setActivePinia(createPinia());
});

describe('need-01 P2 合并舱渲染', () => {
  it('8 个无正文 turn 的 entries → 只渲染 1 个汇总舱（G1）', () => {
    const wrapper = mountTimeline(buildTurnRuns(ghostEntries(8)));
    expect(wrapper.findAll('[data-testid="turn-run-item"]')).toHaveLength(1);
    // 逐个 TurnGroupItem 已被吞掉：折叠栏只剩合并舱那一条
    expect(wrapper.findAll('[data-testid="turn-thinking-tools"]')).toHaveLength(0);
  });

  it('汇总行承载 步骤数 / 工具数 / 失败数（G2，不展开即可决策）', () => {
    // 8 步 × 3 工具 = 24 个工具；合计失败 2 个
    const items = buildTurnRuns(ghostEntries(8, 3, 0));
    const withFails = items.map((e) => (e.kind === 'turn-run' && e.run ? { ...e, run: { ...e.run, failedCount: 2 } } : e));
    const wrapper = mountTimeline(withFails);
    const summary = wrapper.find('[data-testid="turn-run-summary"]').text();
    expect(summary).toContain('8 步');
    expect(summary).toContain('24 个工具');
    expect(summary).toContain('失败 2');
    // 状态词只出现一次（05 陷阱 10），且带耗时后缀（口径沿用 turnDurationLabel）
    expect(wrapper.findAll('[data-testid="turn-run-phase"]')).toHaveLength(1);
    expect(wrapper.find('[data-testid="turn-run-phase"]').text()).toMatch(/^失败( · For \d+s)?$/);
  });

  it('失败 ≥3 时出现「红点 ×N」计数气泡', () => {
    const items = buildTurnRuns(ghostEntries(4, 2, 1)).map((e) =>
      e.kind === 'turn-run' && e.run ? { ...e, run: { ...e.run, failedCount: 3 } } : e
    );
    const wrapper = mountTimeline(items);
    expect(wrapper.find('[data-testid="turn-run-failed-dot-count"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="turn-run-failed-dot-count"]').text()).toBe('×3');
  });

  it('展开后按 iteration 分节，明细能力不倒退（G3）', () => {
    const run = makeTurnRun(
      buildTurnGroups([...ghostStep(1, 2), ...ghostStep(2, 1), ...ghostStep(3, 0, 0, { reasoning: '先看一眼目录' })])
    );
    const wrapper = mount(
      {
        // ToolCallCard 内部 useMessage() 需要外层 provider（naive 的硬要求），故包一层
        components: { NMessageProvider: naive.NMessageProvider, TurnRunItem },
        setup: () => ({ run, cardFor, toolKey }),
        template: `<n-message-provider><TurnRunItem :run="run" :expanded="true" :card-for="cardFor" :tool-key="toolKey" /></n-message-provider>`,
      },
      { global: { plugins: [naive] } }
    );
    expect(wrapper.find('[data-testid="turn-run-steps"]').exists()).toBe(true);
    expect(wrapper.findAll('[data-testid^="turn-run-step-"]')).toHaveLength(3);
  });

  it('有正文的 turn 仍渲染 TurnGroupItem，不出现合并舱', () => {
    const msgs = [msg('u1', 'user', '问'), msg('a1', 'assistant', '这是正文回答')];
    const raw = buildTimelineEntries(msgs, buildTurnGroups(msgs));
    const wrapper = mountTimeline(buildTurnRuns(raw));
    expect(wrapper.findAll('[data-testid="turn-run-item"]')).toHaveLength(0);
    expect(wrapper.text()).toContain('这是正文回答');
  });

  it('kill switch 开启（不聚合）→ 8 个独立折叠栏，与改动前一致（G6）', () => {
    const wrapper = mountTimeline(ghostEntries(8));
    expect(wrapper.findAll('[data-testid="turn-run-item"]')).toHaveLength(0);
    expect(wrapper.findAll('[data-testid="turn-thinking-tools"]')).toHaveLength(8);
  });

  it('单步无正文不合并（D1 = A）：1 个 ghost turn 仍是普通折叠栏', () => {
    const wrapper = mountTimeline(buildTurnRuns(ghostEntries(1)));
    expect(wrapper.findAll('[data-testid="turn-run-item"]')).toHaveLength(0);
    expect(wrapper.findAll('[data-testid="turn-thinking-tools"]')).toHaveLength(1);
  });
});
