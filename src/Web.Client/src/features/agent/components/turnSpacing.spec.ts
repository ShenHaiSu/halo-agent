/**
 * 稳态间距验收（用户轮单容器）。
 *
 * 背景（问题根因）：消息块原先用 Tailwind `space-y-2` 管内部间距，而它是
 * 「兄弟数量驱动」（`.space-y-2 > * + * { margin-top: .5rem }`）。于是「状态行 /
 * 正文 / 折叠栏」任一块的 v-if 变化，都会让后续兄弟的 margin-top 在 0 与 8px
 * 之间摆动 → 用户看到「有时松有时紧」。
 *
 * 本次改造把间距换成「元素自身显式 margin」：
 *   · 根容器 .turn-body 恒为「首个子元素 margin-top: 0，其余 +8px」；
 *   · 单容器三段（user 上 / 过程中 / 末段下）顺序恒定，流稳同槽。
 *
 * 因此本测试锁定两条不变量：
 *   1. 用户轮根容器不再使用 space-y-*（防回归，这是抖动源头）；
 *   2. 单步直出 / 多步分段渲染结构稳定（user-turn-single / process-body / turn-final）。
 *
 * 说明：jsdom 不做布局计算（getBoundingClientRect 恒为 0），所以这里断言的是
 * **决定间距的 DOM/CSS 契约**，而非像素值。
 */
import { describe, expect, it, beforeEach } from 'vitest';
import { mount } from '@vue/test-utils';
import naive from 'naive-ui';
import { NMessageProvider } from 'naive-ui';
import { createPinia, setActivePinia } from 'pinia';
import UserTurnBlock from '@features/agent/components/UserTurnBlock.vue';
import { buildUserTurns, type UserTurn } from '@features/session/utils/userTurn';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import type { AgentToolCard } from '@features/agent/stores/model';

type Extra = Record<string, unknown>;
const msg = (id: string, role: string, content: string | null, extra: Extra = {}): UnifiedChatMessage =>
  ({ id, role, content, createdAtUtc: '2026-10-24T00:00:00Z', ...extra }) as unknown as UnifiedChatMessage;

const cardFor = (m: UnifiedChatMessage) => ({ callId: String(m.id), functionName: 'tool' }) as unknown as AgentToolCard;
const toolKey = (m: UnifiedChatMessage) => String(m.id);

function turnOf(msgs: UnifiedChatMessage[]): UserTurn {
  const entries = buildUserTurns(msgs);
  const e = entries[0];
  if (!e || e.kind !== 'user-turn') throw new Error('fixture 未产出用户轮');
  return (e as unknown as { turn: UserTurn }).turn;
}

function mountTurn(turn: UserTurn, props: Extra = {}) {
  return mount(
    {
      components: { NMessageProvider, UserTurnBlock },
      setup: () => ({ turn, cardFor, toolKey, props }),
      template: `<n-message-provider>
        <UserTurnBlock :turn="turn" :expanded="true" :render-html="(t) => t" :card-for="cardFor" :tool-key="toolKey" v-bind="props" />
      </n-message-provider>`,
    },
    { global: { plugins: [naive] } }
  );
}

beforeEach(() => {
  setActivePinia(createPinia());
});

/** 单步纯正文：user + 单 assistant，无工具。 */
const flatOnly = () => turnOf([msg('u1', 'user', '这是提问'), msg('a1', 'assistant', '这是正文回答')]);

/** 单步带工具：user + 含 toolCalls 的 assistant + tool 回包。 */
const flatWithTools = () =>
  turnOf([
    msg('u1', 'user', '这是提问', {}),
    msg('a1', 'assistant', '这是正文回答', {
      toolCalls: [{ id: 'c1', type: 'function', function: { name: 'tool', arguments: '{}' } }],
    }),
    msg('t1', 'tool', 'ok', { toolCallId: 'c1' }),
  ]);

/** 多步：user + 两段 assistant（上折叠区 + 末段直出）。 */
const multiStep = () => turnOf([msg('u1', 'user', '这是提问'), msg('a1', 'assistant', '第一段'), msg('a2', 'assistant', '末段')]);

describe('用户轮单容器间距不再依赖兄弟数量', () => {
  it('UserTurnBlock 根容器使用 .turn-body，且不再出现 space-y-*（抖动源头）', () => {
    const wrapper = mountTurn(flatWithTools());
    expect(wrapper.find('.turn-body').exists()).toBe(true);
    // 关键回归锁：space-y-* 必须从消息块内部彻底消失
    expect(wrapper.html()).not.toMatch(/class="[^"]*\bspace-y-\d/);
  });

  it('单步纯正文直出 user-turn-single', () => {
    const wrapper = mountTurn(flatOnly());
    expect(wrapper.find('[data-testid="user-turn-single"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="process-body"]').exists()).toBe(false);
  });

  it('多步渲染过程区 process-body 与末段 turn-final', () => {
    const wrapper = mountTurn(multiStep());
    expect(wrapper.find('[data-testid="process-body"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="turn-final"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="user-turn-step-1"]').exists()).toBe(true);
  });

  it('单步带工具走分段栈，同样无 space-y-*', () => {
    const wrapper = mountTurn(flatWithTools());
    expect(wrapper.html()).not.toMatch(/class="[^"]*\bspace-y-\d/);
    expect(wrapper.find('[data-testid="process-body"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="turn-final"]').exists()).toBe(true);
  });
});
