/**
 * need23：智能体行为页签·子代理卡片单测。
 * 覆盖点：
 * - 子代理配置卡：控件齐全 + emit 字段名（写入用后端字段名 / 恢复默认用后端 reset 别名）+ 数值边界裁剪。
 * - 子运行监控卡：真实统计列 + 每行"历史"传**完整** subId（此前被截断成 8 字符，enterSub 必然失败）
 *   + 拒绝告警用后端真实时间（此前前端伪造 now()）。
 */
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import naive from 'naive-ui';
import SubAgentConfigCard from './SubAgentConfigCard.vue';
import SubRuntimeMonitorCard from './SubRuntimeMonitorCard.vue';
import { clampSubNum, SUB_MAX_ITERATIONS_MAX, SUB_MAX_PARALLEL_MAX, type SubAgentConfig } from './subAgentFields';
import type { SubRuntimeRow } from './subRuntimeTypes';

const cfg: SubAgentConfig = {
  subAgentEnabled: true,
  subMaxParallel: 3,
  subMaxIterations: 15,
  subAllowExecCommand: true,
  subAllowFileWrite: true,
  subAllowSkillWrite: false,
  subAllowMemoryWrite: false,
  subAllowAskUser: false,
};

const FULL_SUB_ID = 'sess_abcdef0123456789';
const rows: SubRuntimeRow[] = [
  {
    subId: FULL_SUB_ID,
    shortId: FULL_SUB_ID.slice(0, 8),
    label: '子任务A',
    status: 'running',
    iteration: 3,
    toolCount: 7,
    lastTool: 'read_file',
    elapsedMs: 1234,
  },
];

function mountConfigCard() {
  return mount(SubAgentConfigCard, { props: { cfg }, global: { plugins: [naive] } });
}
function mountMonitorCard(overrides: Record<string, unknown> = {}) {
  return mount(SubRuntimeMonitorCard, {
    props: {
      rows,
      loading: false,
      policyText: '快照：开 / 并发3 / 轮次15',
      rejection: null,
      ...overrides,
    },
    global: { plugins: [naive] },
  });
}

describe('SubAgentConfigCard', () => {
  it('渲染总开关/并发/轮次/4 个权限开关/ask_user', () => {
    const w = mountConfigCard();
    for (const id of [
      'agent-sub-enabled',
      'agent-sub-parallel-input',
      'agent-sub-iterations-input',
      'agent-sub-perm-exec',
      'agent-sub-perm-file',
      'agent-sub-perm-skill',
      'agent-sub-perm-memory',
      'agent-sub-perm-ask',
    ]) {
      expect(w.find(`[data-testid="${id}"]`).exists(), id).toBe(true);
    }
  });

  it('总开关 emit bool + 后端字段名 subAgentEnabled', async () => {
    const w = mountConfigCard();
    const switches = w.findAllComponents({ name: 'Switch' });
    expect(switches.length).toBeGreaterThanOrEqual(6);
    await switches[0].vm.$emit('update:value', false);
    expect(w.emitted('bool')?.[0]).toEqual(['subAgentEnabled', false]);
  });

  it('权限开关 emit 对应后端字段名', async () => {
    const w = mountConfigCard();
    const switches = w.findAllComponents({ name: 'Switch' });
    // 顺序：总开关 / exec / 文件写 / skill 写 / memory 写 / ask_user(锁定)
    await switches[1].vm.$emit('update:value', false);
    await switches[4].vm.$emit('update:value', true);
    expect(w.emitted('bool')).toEqual([
      ['subAllowExecCommand', false],
      ['subAllowMemoryWrite', true],
    ]);
  });

  it('并发/轮次 emit num + 后端字段名', async () => {
    const w = mountConfigCard();
    const nums = w.findAllComponents({ name: 'InputNumber' });
    expect(nums.length).toBe(2);
    await nums[0].vm.$emit('update:value', 5);
    await nums[1].vm.$emit('update:value', 42);
    expect(w.emitted('num')).toEqual([
      ['subMaxParallel', 5],
      ['subMaxIterations', 42],
    ]);
  });

  it('恢复默认 emit 后端 reset 别名', async () => {
    const w = mountConfigCard();
    await w.find('[data-testid="agent-sub-reset-enabled"]').trigger('click');
    await w.find('[data-testid="agent-sub-reset-parallel"]').trigger('click');
    await w.find('[data-testid="agent-sub-reset-iterations"]').trigger('click');
    await w.find('[data-testid="agent-sub-reset-perms"]').trigger('click');
    expect(w.emitted('reset')).toEqual([['subEnabled'], ['subParallel'], ['subIterations'], ['subPerms']]);
  });
});

describe('clampSubNum（写 agent.json 前的边界裁剪）', () => {
  it('清空输入不回写 0（后端 AGENT_VALIDATION 会拒）', () => {
    expect(clampSubNum('subMaxParallel', null)).toBe(1);
    expect(clampSubNum('subMaxIterations', undefined)).toBe(1);
  });
  it('裁剪到后端区间：并发 1..8，轮次 1..100', () => {
    expect(clampSubNum('subMaxParallel', 0)).toBe(1);
    expect(clampSubNum('subMaxParallel', 99)).toBe(SUB_MAX_PARALLEL_MAX);
    expect(clampSubNum('subMaxIterations', 0)).toBe(1);
    expect(clampSubNum('subMaxIterations', 999)).toBe(SUB_MAX_ITERATIONS_MAX);
  });
  it('区间内的值原样保留', () => {
    expect(clampSubNum('subMaxParallel', 4)).toBe(4);
    expect(clampSubNum('subMaxIterations', 100)).toBe(100);
  });
});

describe('SubRuntimeMonitorCard', () => {
  it('渲染运行表格与真实统计列', () => {
    const w = mountMonitorCard();
    expect(w.find('[data-testid="agent-runtime-table"]').exists()).toBe(true);
    expect(w.text()).toContain('子任务A');
    expect(w.text()).toContain('read_file');
  });

  it('每行"历史"按钮传完整 subId（不截断）', async () => {
    const w = mountMonitorCard();
    const btns = w.findAll('[data-testid="agent-sub-open-history"]');
    expect(btns.length).toBe(1);
    await btns[0].trigger('click');
    expect(w.emitted('openHistory')?.[0]).toEqual([FULL_SUB_ID]);
  });

  it('拒绝告警展示后端真实 code/callId', () => {
    const w = mountMonitorCard({
      rejection: {
        code: 'SUBAGENT_QUOTA_EXCEEDED',
        callId: 'call_1',
        atUtc: '2026-10-21T08:00:00.000Z',
      },
    });
    const el = w.find('[data-testid="agent-sub-rejection"]');
    expect(el.exists()).toBe(true);
    expect(el.text()).toContain('SUBAGENT_QUOTA_EXCEEDED');
    expect(el.text()).toContain('call_1');
  });

  it('无运行中的子代理时显示空态', () => {
    const w = mountMonitorCard({ rows: [] });
    expect(w.text()).toContain('当前无子代理运行');
  });

  it('刷新 / 掐断整树 事件可触发', async () => {
    const w = mountMonitorCard();
    await w.find('[data-testid="agent-sub-refresh"]').trigger('click');
    await w.find('[data-testid="agent-sub-cancel-tree"]').trigger('click');
    expect(w.emitted('refresh')).toHaveLength(1);
    expect(w.emitted('cancelTree')).toHaveLength(1);
  });
});
