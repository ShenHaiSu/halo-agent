import { describe, expect, it } from 'vitest';
import { mount, type VueWrapper } from '@vue/test-utils';
import naive from 'naive-ui';
import type { Component, ComponentPublicInstance } from 'vue';
import AgentTimeoutCard from './AgentTimeoutCard.vue';
import AgentIterationsCard from './AgentIterationsCard.vue';
import SubAgentConfigCard from './SubAgentConfigCard.vue';
import SubRuntimeMonitorCard from './SubRuntimeMonitorCard.vue';
import RejectionLogCard from './RejectionLogCard.vue';
import type { SubAgentConfig } from './subAgentFields';
import type { SubRuntimeRow } from './subRuntimeTypes';

const baseCfg: SubAgentConfig = {
  subAgentEnabled: true,
  subMaxParallel: 3,
  subMaxIterations: 15,
  subAllowExecCommand: true,
  subAllowFileWrite: true,
  subAllowSkillWrite: false,
  subAllowMemoryWrite: false,
  subAllowAskUser: false,
};

const runtimeRows: SubRuntimeRow[] = [
  {
    subId: 'sess-full-identifier',
    shortId: 'sess-ful',
    label: '子任务',
    status: 'running',
    iteration: 2,
    toolCount: 18,
    lastTool: 'read_file',
    elapsedMs: 128400,
  },
];

function mountWith(component: Component, props: Record<string, unknown>): VueWrapper<ComponentPublicInstance> {
  return mount(component as never, {
    props: props as never,
    global: { plugins: [naive] },
  }) as unknown as VueWrapper<ComponentPublicInstance>;
}

describe('业务卡片：脏字段行标记', () => {
  it('超时卡只标记发生变化的字段行', () => {
    const wrapper = mountWith(AgentTimeoutCard, {
      cfg: { execDefaultTimeoutMs: 30000, execMaxTimeoutMs: 300000 },
      dirtyFields: ['execDefaultTimeoutMs'],
    });
    const rows = wrapper.findAll('.agent-setting-row');
    expect(rows[0].classes()).toContain('is-changed');
    expect(rows[1].classes()).not.toContain('is-changed');
  });

  it('轮次卡对无限时间开关值也标记脏态', () => {
    const wrapper = mountWith(AgentIterationsCard, {
      cfg: { maxIterations: 25, turnTimeoutMs: 0 },
      dirtyFields: ['turnTimeoutMs'],
    });
    const rows = wrapper.findAll('.agent-setting-row');
    expect(rows[0].classes()).not.toContain('is-changed');
    expect(rows[1].classes()).toContain('is-changed');
  });

  it('子代理卡把权限字段映射到同一行', () => {
    const wrapper = mountWith(SubAgentConfigCard, { cfg: baseCfg, dirtyFields: ['subAllowMemoryWrite'] });
    const changed = wrapper.findAll('.agent-setting-row.is-changed');
    expect(changed).toHaveLength(1);
    expect(changed[0].text()).toContain('memory 写');
  });
});

describe('业务卡片：轮次与无限时间', () => {
  it('开启无限时间写入 0，关闭写入默认 600000', async () => {
    const wrapper = mountWith(AgentIterationsCard, { cfg: { maxIterations: 25, turnTimeoutMs: 600000 } });
    const toggle = wrapper.get('[data-testid="agent-turn-timeout-infinite"]').findComponent({ name: 'Switch' });
    await toggle.vm.$emit('update:value', true);
    await toggle.vm.$emit('update:value', false);
    expect(wrapper.emitted('update')).toEqual([
      ['turnTimeoutMs', 0],
      ['turnTimeoutMs', 600000],
    ]);
  });

  it('无限时间时输入控件禁用', () => {
    const wrapper = mountWith(AgentIterationsCard, { cfg: { maxIterations: 25, turnTimeoutMs: 0 } });
    const input = wrapper.get('[data-testid="agent-turn-timeout-input"]').findComponent({ name: 'InputNumber' });
    expect(input.props('disabled')).toBe(true);
  });
});

describe('业务卡片：子代理权限联动', () => {
  it('总开关关闭时容量与权限控件禁用但值保留', () => {
    const wrapper = mountWith(SubAgentConfigCard, { cfg: { ...baseCfg, subAgentEnabled: false } });
    const numbers = wrapper.findAllComponents({ name: 'InputNumber' });
    expect(numbers).toHaveLength(2);
    expect(numbers[0].props('disabled')).toBe(true);
    expect(numbers[1].props('disabled')).toBe(true);
    expect(wrapper.find('[data-testid="agent-sub-parallel-input"]').exists()).toBe(true);
    expect(wrapper.get('[data-testid="agent-sub-enabled"]').findComponent({ name: 'Switch' }).props('value')).toBe(false);
    expect(wrapper.find('.is-section-disabled').exists()).toBe(true);
  });

  it('开启 skill 写或 memory 写时显示风险提示', () => {
    const skill = mountWith(SubAgentConfigCard, { cfg: { ...baseCfg, subAllowSkillWrite: true } });
    const memory = mountWith(SubAgentConfigCard, { cfg: { ...baseCfg, subAllowMemoryWrite: true } });
    const clean = mountWith(SubAgentConfigCard, { cfg: baseCfg });
    expect(skill.find('[data-testid="agent-sub-risk-alert"]').exists()).toBe(true);
    expect(memory.find('[data-testid="agent-sub-risk-alert"]').exists()).toBe(true);
    expect(clean.find('[data-testid="agent-sub-risk-alert"]').exists()).toBe(false);
  });

  it('ask_user 开关始终禁用且不发出事件', async () => {
    const wrapper = mountWith(SubAgentConfigCard, { cfg: baseCfg });
    const ask = wrapper.get('[data-testid="agent-sub-perm-ask"]').findComponent({ name: 'Switch' });
    expect(ask.props('disabled')).toBe(true);
    await ask.vm.$emit('update:value', true);
    expect(wrapper.emitted('bool')).toBeUndefined();
  });
});

describe('业务卡片：监控与拒绝记录', () => {
  it('无运行子代理时显示空态', () => {
    const wrapper = mountWith(SubRuntimeMonitorCard, { rows: [], loading: false, policyText: '', rejection: null });
    expect(wrapper.text()).toContain('当前无子代理运行');
  });

  it('策略文本来自后端快照属性而非本地编辑值', () => {
    const policyText = '快照：已启用 · 并发 3 · 每子 15 轮 · exec · file';
    const wrapper = mountWith(SubRuntimeMonitorCard, { rows: runtimeRows, loading: false, policyText, rejection: null });
    expect(wrapper.text()).toContain(policyText);
  });

  it('拒绝记录展示后端 code、callId 与真实时间', () => {
    const atUtc = '2026-10-21T08:00:00.000Z';
    const wrapper = mountWith(RejectionLogCard, {
      rows: [{ code: 'SUBAGENT_QUOTA_EXCEEDED', callId: 'call-a', atUtc }],
    });
    const row = wrapper.get('[data-testid="agent-rejection-row"]');
    expect(row.text()).toContain('SUBAGENT_QUOTA_EXCEEDED');
    expect(row.text()).toContain('call-a');
    expect(row.text()).not.toContain('Invalid Date');
  });

  it('空拒绝记录显示占位文案', () => {
    const wrapper = mountWith(RejectionLogCard, { rows: [] });
    expect(wrapper.text()).toContain('暂无拒绝记录');
  });
});
