// need02-P2：UserTurnBlock/Summary 组件断言（06 §6.2）。
import { beforeEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import naive from 'naive-ui';
import { createPinia, setActivePinia } from 'pinia';
import UserTurnBlock from '@features/agent/components/UserTurnBlock.vue';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import { buildUserTurns } from '@features/session/utils/userTurn';

const u = (id: string, content: string): UnifiedChatMessage =>
  ({ id, role: 'user', content, createdAtUtc: '2026-10-26T11:59:00Z' }) as unknown as UnifiedChatMessage;
const a = (id: string, content: string, extra: Record<string, unknown> = {}): UnifiedChatMessage =>
  ({ id, role: 'assistant', content, createdAtUtc: '2026-10-26T11:59:10Z', ...extra }) as unknown as UnifiedChatMessage;
const t = (id: string, callId: string, content: string): UnifiedChatMessage =>
  ({ id, role: 'tool', toolCallId: callId, content, createdAtUtc: '2026-10-26T11:59:20Z' }) as unknown as UnifiedChatMessage;

function mountBlock(turn: ReturnType<typeof buildUserTurns>[number], opts: { expanded?: boolean; pendingStatus?: string } = {}) {
  if (turn.kind !== 'user-turn') throw new Error('not a user turn');
  return mount(UserTurnBlock, {
    props: {
      turn: turn.turn,
      expanded: opts.expanded ?? false,
      renderHtml: (s: string) => `<p>${s}</p>`,
      cardFor: (m: UnifiedChatMessage) => ({ callId: String(m.id), functionName: 'tool' }) as never,
      toolKey: (m: UnifiedChatMessage) => String(m.id),
      msgImages: () => [],
      thumbOf: () => '',
      imgLost: () => false,
      liveSegs: [],
      streamingText: '',
      isGenerating: false,
      pendingStatus: opts.pendingStatus ?? '',
    },
    global: {
      plugins: [naive, createPinia()],
      // ToolCallCard setup 内直连 useMessage + workspaceStore；此处只断言轮次结构，卡片打桩避免 provider 包裹。
      stubs: { ToolCallCard: { template: '<div class="tool-card-stub" />' } },
    },
  });
}

beforeEach(() => {
  setActivePinia(createPinia());
});

describe('need02-P2 UserTurnBlock 渲染', () => {
  it('3 iteration 混合 → 1 折叠行（过程内容/工具5/失败1）+ 下末段直出', () => {
    const msgs = [
      u('u1', '查一下'),
      a('a1', '第一段'),
      t('t1', 'c1', 'out1'),
      t('t2', 'c2', '[Tool Error] boom'),
      a('a2', ''),
      t('t3', 'c3', 'out3'),
      t('t4', 'c4', 'out4'),
      t('t5', 'c5', 'out5'),
      a('a3', '末段结论全文'),
    ];
    const entries = buildUserTurns(msgs, { now: Date.parse('2026-10-26T12:00:00Z') });
    const wrapper = mountBlock(entries[0], { expanded: true });
    expect(wrapper.findAll('[data-testid="user-turn-block"]')).toHaveLength(1);
    const summary = wrapper.find('[data-testid="user-turn-summary"]');
    expect(summary.exists()).toBe(true);
    expect(summary.text()).toContain('过程内容');
    expect(summary.text()).toContain('工具5');
    expect(summary.text()).toContain('失败1');
    // 下末段正文直出（全文）
    expect(wrapper.html()).toContain('末段结论全文');
    // 展开区 2 节（非末段），不重复末段
    expect(wrapper.findAll('[data-testid^="user-turn-step-"]')).toHaveLength(2);
  });

  it('单步无工具 → 无汇总行，正文直出', () => {
    const entries = buildUserTurns([u('u1', '你好'), a('a1', '世界')], { now: Date.now() });
    const wrapper = mountBlock(entries[0]);
    expect(wrapper.find('[data-testid="user-turn-summary"]').exists()).toBe(false);
    const single = wrapper.find('[data-testid="user-turn-single"]');
    expect(single.exists()).toBe(true);
    expect(single.html()).toContain('世界');
  });

  it('pending sending → 发送中', () => {
    const entries = buildUserTurns([u('pending-req1', '发送中')], { now: Date.now() });
    const wrapper = mountBlock(entries[0], { pendingStatus: 'sending' });
    expect(wrapper.find('[data-testid="pending-sending"]').exists()).toBe(true);
  });

  it('pending failed → 标红 + 重发按钮', async () => {
    const entries = buildUserTurns([u('pending-req1', '发送中')], { now: Date.now() });
    const wrapper = mountBlock(entries[0], { pendingStatus: 'failed' });
    expect(wrapper.find('[data-testid="pending-failed"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="pending-retry-btn"]').exists()).toBe(true);
    await wrapper.find('[data-testid="pending-retry-btn"]').trigger('click');
    expect(wrapper.emitted('retry-pending')).toBeTruthy();
  });

  it('失败≥3 → 红点 ×N + 状态 失败', () => {
    const msgs = [u('u1', 'x'), a('a1', 'y'), t('t1', 'c1', '[Tool Error] 1'), t('t2', 'c2', '[Tool Error] 2'), t('t3', 'c3', '[Tool Error] 3')];
    const entries = buildUserTurns(msgs, { now: Date.now() });
    const wrapper = mountBlock(entries[0]);
    expect(wrapper.find('[data-testid="user-turn-failed-dot-count"]').text()).toContain('×3');
    expect(wrapper.find('[data-testid="user-turn-phase"]').text()).toContain('失败');
  });

  it('展开区按 step 分节（含 tool 卡槽位）', () => {
    const msgs = [u('u1', 'x'), a('a1', '第一段'), t('t1', 'c1', 'out'), a('a2', '末段')];
    const entries = buildUserTurns(msgs, { now: Date.now() });
    const wrapper = mountBlock(entries[0], { expanded: true });
    expect(wrapper.findAll('[data-testid^="user-turn-step-"]')).toHaveLength(1);
    expect(wrapper.html()).toContain('第一段');
    expect(wrapper.html()).toContain('末段');
  });
});
