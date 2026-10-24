/**
 * need-02 稳态间距稳定性验收。
 *
 * 背景（问题根因）：消息块原先用 Tailwind `space-y-2` 管内部间距，而它是
 * 「兄弟数量驱动」（`.space-y-2 > * + * { margin-top: .5rem }`）。于是「状态行 /
 * 正文 / 折叠栏」任一块的 v-if 变化，都会让后续兄弟的 margin-top 在 0 与 8px
 * 之间摆动 → 用户看到「有时松有时紧」。
 *
 * 本次改造把间距换成「元素自身显式 margin」：
 *   · 根容器 .turn-body 恒为「首个子元素 margin-top: 0，其余 +8px」；
 *   · 状态行整块删除，「已完成 · 3s」下沉为折叠栏 header 右侧角标。
 *
 * 因此本测试锁定三条不变量：
 *   1. 消息块根容器不再使用 space-y-*（防回归，这是抖动源头）；
 *   2. 正文成为消息块的第一个元素（不再有「可选的前置状态行」）；
 *   3. 「已完成」不再以独立行出现，而是折叠栏 header 内的角标。
 *
 * 说明：jsdom 不做布局计算（getBoundingClientRect 恒为 0），所以这里断言的是
 * **决定间距的 DOM/CSS 契约**，而非像素值。像素验收另见 06 § 人工核对表。
 */
import { describe, expect, it, beforeEach } from 'vitest';
import { mount } from '@vue/test-utils';
import naive from 'naive-ui';
import { NMessageProvider } from 'naive-ui';
import { createPinia, setActivePinia } from 'pinia';
import TurnGroupItem from '@features/agent/components/TurnGroupItem.vue';
import TurnRunItem from '@features/agent/components/TurnRunItem.vue';
import { buildTurnGroups, type TurnGroup } from '@features/session/utils/turnGroup';
import { makeTurnRun } from '@features/session/utils/turnRun';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';

type Extra = Record<string, unknown>;
const msg = (id: string, role: string, content: string | null, extra: Extra = {}): UnifiedChatMessage =>
  ({ id, role, content, createdAtUtc: '2026-10-24T00:00:00Z', ...extra }) as unknown as UnifiedChatMessage;

const cardFor = (m: UnifiedChatMessage) => ({ callId: String(m.id), functionName: 'tool' }) as never;
const toolKey = (m: UnifiedChatMessage) => String(m.id);

function groupOf(msgs: UnifiedChatMessage[]): TurnGroup {
  const g = buildTurnGroups(msgs)[0];
  if (!g) throw new Error('fixture 未产出 TurnGroup');
  return g;
}

function mountGroup(group: TurnGroup, props: Extra = {}) {
  return mount(
    {
      components: { NMessageProvider, TurnGroupItem },
      setup: () => ({ group, cardFor, toolKey, props }),
      template: `<n-message-provider>
        <TurnGroupItem :group="group" :expanded="false" :has-failed="false"
          :show-telemetry="false" protocol="" cache-line=""
          :render-html="(t) => t" :card-for="cardFor" :tool-key="toolKey" v-bind="props" />
      </n-message-provider>`,
    },
    { global: { plugins: [naive] } }
  );
}

beforeEach(() => {
  setActivePinia(createPinia());
});

/** 有正文 + 有工具：改造前「状态行 + 正文 + 折叠栏」三个兄弟。 */
const withContentAndTools = () =>
  groupOf([
    msg('a1', 'assistant', '这是正文回答', {
      toolCalls: [{ id: 'c1', type: 'function', function: { name: 'tool', arguments: '{}' } }],
    }),
    msg('t1', 'tool', 'ok', { toolCallId: 'c1' }),
  ]);

/** 纯正文、无工具无 reasoning：改造前「状态行 + 正文」两个兄弟。 */
const contentOnly = () => groupOf([msg('a1', 'assistant', '这是正文回答')]);

/** 无正文、只有工具：改造前「状态行 + 折叠栏」。 */
const toolsOnly = () =>
  groupOf([
    msg('a1', 'assistant', null, {
      toolCalls: [{ id: 'c1', type: 'function', function: { name: 'tool', arguments: '{}' } }],
    }),
    msg('t1', 'tool', 'ok', { toolCallId: 'c1' }),
  ]);

describe('need-02 消息块间距不再依赖兄弟数量', () => {
  it('TurnGroupItem 根容器使用 .turn-body，且不再出现 space-y-*（抖动源头）', () => {
    const wrapper = mountGroup(withContentAndTools());
    const root = wrapper.find('.turn-body');
    expect(root.exists()).toBe(true);
    // 关键回归锁：space-y-* 必须从消息块内部彻底消失
    expect(wrapper.html()).not.toMatch(/class="[^"]*\bspace-y-\d/);
  });

  it('TurnRunItem 根容器同样使用 .turn-body，无 space-y-*', () => {
    const run = makeTurnRun([toolsOnly(), toolsOnly()]);
    const wrapper = mount(TurnRunItem, {
      props: { run, expanded: false, cardFor, toolKey },
      global: { plugins: [naive] },
    });
    expect(wrapper.find('.turn-body').exists()).toBe(true);
    expect(wrapper.html()).not.toMatch(/class="[^"]*\bspace-y-\d/);
  });

  it('正文是消息块的第一个元素（前面没有可选的「状态行」兄弟）', () => {
    const wrapper = mountGroup(withContentAndTools());
    const first = wrapper.find('.turn-body').element.firstElementChild as HTMLElement;
    expect(first.className).toContain('turn-content');
  });

  it('折叠栏是 .turn-body 的兄弟且紧跟正文（间距由显式 margin 承担）', () => {
    const wrapper = mountGroup(withContentAndTools());
    const children = Array.from(wrapper.find('.turn-body').element.children) as HTMLElement[];
    expect(children).toHaveLength(2);
    expect(children[0].className).toContain('turn-content');
    expect(children[1].getAttribute('data-testid')).toBe('turn-thinking-tools');
  });

  it('纯正文（无工具无 reasoning）只有正文一个子元素 —— 不会因「少一块」而让正文被额外顶开', () => {
    const wrapper = mountGroup(contentOnly());
    const children = Array.from(wrapper.find('.turn-body').element.children);
    expect(children).toHaveLength(1);
    expect(wrapper.find('[data-testid="turn-thinking-tools"]').exists()).toBe(false);
  });

  it('无正文时折叠栏是首个子元素（上边距 0，直接贴消息块顶部）', () => {
    const wrapper = mountGroup(toolsOnly());
    const first = wrapper.find('.turn-body').element.firstElementChild as HTMLElement;
    expect(first.getAttribute('data-testid')).toBe('turn-thinking-tools');
  });
});

describe('need-02「已完成」不再独占一行', () => {
  it('TurnGroupItem 不再渲染任何顶层状态行文本（「已完成」只作为折叠栏角标）', () => {
    const wrapper = mountGroup(withContentAndTools());
    const children = Array.from(wrapper.find('.turn-body').element.children) as HTMLElement[];
    // 顶层不得再出现「已完成」独立行：正文与折叠栏之外没有第三个块
    for (const child of children) {
      expect(child.textContent?.trim().startsWith('已完成')).toBeFalsy();
    }
    // 角标存在，且只出现一次（在折叠栏 header 内）
    const badges = wrapper.findAll('[data-testid="turn-phase-badge"]');
    expect(badges).toHaveLength(1);
    expect(badges[0].text()).toMatch(/^已完成( · For \d+s)?$/);
  });

  it('失败态：角标醒目（rose），且折叠栏左侧竖线换红 —— 不额外占行', () => {
    const wrapper = mountGroup(withContentAndTools(), { hasFailed: true });
    const badge = wrapper.find('[data-testid="turn-phase-badge"]');
    expect(badge.text()).toMatch(/^失败/);
    expect(badge.classes().join(' ')).toContain('text-rose-500');
    expect(wrapper.find('[data-testid="turn-thinking-tools"]').classes().join(' ')).toContain('border-rose-500');
  });

  it('已完成态：角标静默（opacity-60）且竖线维持 indigo', () => {
    const wrapper = mountGroup(withContentAndTools());
    const badge = wrapper.find('[data-testid="turn-phase-badge"]');
    expect(badge.classes().join(' ')).toContain('opacity-60');
    expect(wrapper.find('[data-testid="turn-thinking-tools"]').classes().join(' ')).toContain('border-indigo-500/70');
  });

  it('运行中：折叠栏图标位换成 spinner（原状态行的呼吸指示随之迁移）', () => {
    const wrapper = mountGroup(withContentAndTools(), { isActive: true });
    expect(wrapper.find('[data-testid="turn-phase-spinner"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="turn-phase-badge"]').text()).toMatch(/^运行中…/);
  });
});

describe('need-02 RunStepList 同步收敛', () => {
  it('步骤容器用 .run-steps，不再用 space-y-2', () => {
    const run = makeTurnRun([toolsOnly(), toolsOnly()]);
    // expanded=true 会真正渲染 ToolCallCard，其 setup 里 useMessage() 需要外层 provider
    const wrapper = mount(
      {
        components: { NMessageProvider, TurnRunItem },
        setup: () => ({ run, cardFor, toolKey }),
        template: `<n-message-provider><TurnRunItem :run="run" :expanded="true" :card-for="cardFor" :tool-key="toolKey" /></n-message-provider>`,
      },
      { global: { plugins: [naive] } }
    );
    const steps = wrapper.find('[data-testid="turn-run-steps"]');
    expect(steps.exists()).toBe(true);
    expect(steps.classes()).toContain('run-steps');
    expect(steps.classes()).not.toContain('space-y-2');
  });
});
