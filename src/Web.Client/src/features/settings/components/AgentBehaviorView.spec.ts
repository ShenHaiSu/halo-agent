/**
 * 智能体行为页（设置与诊断中心 → 智能体行为）集成测试。
 *
 * 覆盖此前全是**静默失败**的三类问题：
 * 1. 子代理配置卡 / 子运行监控卡：模板用了但没 import → Vue 编译成 resolveComponent("Xxx")
 *    → 运行时解析失败 → 整卡渲染为空白（界面无卡、无报错、无日志）。
 *    本测试直接断言两张卡的关键 testid 存在。
 * 2. "目标摘要"卡已删除 → 断言 agent-goal-summary 不存在。
 * 3. 子会话 id 被截断成 8 字符 → "历史"按钮必须传**完整** subId，否则 enterSub 必然找不到子会话。
 */
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import { createPinia, setActivePinia, type Pinia } from 'pinia';
import naive from 'naive-ui';
import { defineComponent, h } from 'vue';

const { bridgeStub, enterSubMock, dialogStub } = vi.hoisted(() => ({
  bridgeStub: {
    getAgentConfig: vi.fn(),
    saveAgentConfig: vi.fn(),
    resetAgentConfig: vi.fn(),
    getSubRuntime: vi.fn(),
    cancelAgentTree: vi.fn(),
  },
  enterSubMock: vi.fn(),
  dialogStub: { warning: vi.fn(), error: vi.fn(), success: vi.fn() },
}));

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...actual, getHostBridge: () => bridgeStub };
});

vi.mock('@/stores/subSessionStore', () => ({
  useSubSessionStore: () => ({ enterSub: enterSubMock }),
}));

// naive-ui 的 useMessage/useDialog 需要 Provider 祖先；单测里替换为无副作用 stub
vi.mock('naive-ui', async (importOriginal) => {
  const actual = await importOriginal<typeof import('naive-ui')>();
  return {
    ...actual,
    useMessage: () => ({
      success: vi.fn(),
      error: vi.fn(),
      info: vi.fn(),
      warning: vi.fn(),
    }),
    useDialog: () => dialogStub,
  };
});

import AgentBehaviorView from './AgentBehaviorView.vue';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useAgentConfigStore } from '@/stores/agentConfigStore';
import { haloEventBus } from '@shared/bridge/hostBridge';

const FULL_SUB_ID = 'sess_abcdef0123456789';

const agentConfigDoc = {
  version: 1,
  execDefaultTimeoutMs: 120000,
  execMaxTimeoutMs: 600000,
  maxIterations: 40,
  turnTimeoutMs: 600000,
  subAgentEnabled: true,
  subMaxParallel: 3,
  subMaxIterations: 15,
  subAllowExecCommand: true,
  subAllowFileWrite: true,
  subAllowSkillWrite: false,
  subAllowMemoryWrite: false,
  subAllowAskUser: false,
  scope: 'global',
  updatedAtUtc: '2026-10-21T08:00:00.000Z',
};

const runtimeInfo = {
  subs: [
    {
      subId: FULL_SUB_ID,
      label: '子任务A',
      status: 'running',
      iteration: 3,
      toolCount: 7,
      lastTool: 'read_file',
      elapsedMs: 1234,
    },
  ],
  policy: {
    enabled: true,
    maxParallel: 3,
    maxIterations: 15,
    exec: true,
    file: true,
    skill: false,
    memory: false,
  },
  lastRejection: null,
  recentRejections: [{ code: 'SUBAGENT_QUOTA_EXCEEDED', callId: 'call_1', atUtc: '2026-10-21T08:00:00.000Z' }],
};

const Host = defineComponent({
  setup() {
    return () => h(AgentBehaviorView);
  },
});

async function mountView(pinia: Pinia) {
  const w = mount(Host, { global: { plugins: [naive, pinia] } });
  await flushPromises();
  await flushPromises();
  return w;
}

describe('AgentBehaviorView（设置与诊断中心 → 智能体行为）', () => {
  let pinia: Pinia;

  beforeEach(() => {
    pinia = createPinia();
    setActivePinia(pinia);
    bridgeStub.getAgentConfig.mockReset().mockResolvedValue({ ok: true, data: structuredClone(agentConfigDoc) });
    bridgeStub.getSubRuntime.mockReset().mockResolvedValue({ ok: true, data: structuredClone(runtimeInfo) });
    bridgeStub.cancelAgentTree.mockReset().mockResolvedValue({ ok: true, data: true });
    enterSubMock.mockReset().mockResolvedValue(undefined);
    dialogStub.warning.mockReset();
    dialogStub.error.mockReset();
    const ws = useWorkspaceStore();
    ws.activeWorkspaceId = 'ws-1';
    ws.activeSessionId = 'sess-main';
  });

  it('渲染子代理配置卡（漏 import 会让整卡空白）', async () => {
    const w = await mountView(pinia);
    expect(w.find('[data-testid="agent-sub-enabled"]').exists()).toBe(true);
    expect(w.find('[data-testid="agent-sub-parallel-input"]').exists()).toBe(true);
    expect(w.find('[data-testid="agent-sub-perm-exec"]').exists()).toBe(true);
  });

  it('渲染子运行监控卡（漏 import 会让整卡空白）', async () => {
    const w = await mountView(pinia);
    expect(w.find('[data-testid="agent-runtime-table"]').exists()).toBe(true);
    expect(w.text()).toContain('子任务A');
  });

  it('不再渲染"目标摘要"卡', async () => {
    const w = await mountView(pinia);
    expect(w.find('[data-testid="agent-goal-summary"]').exists()).toBe(false);
    expect(w.text()).not.toContain('目标摘要');
  });

  it('运行表展示后端真实统计（轮次/工具数/最近工具）', async () => {
    const w = await mountView(pinia);
    const text = w.text();
    expect(text).toContain('read_file');
    expect(text).toContain('3');
    expect(text).toContain('7');
  });

  it('拒绝记录用后端真实时间（不再伪造 now()）', async () => {
    const w = await mountView(pinia);
    const el = w.find('[data-testid="agent-sub-rejection"]');
    expect(el.exists()).toBe(true);
    expect(el.text()).toContain('SUBAGENT_QUOTA_EXCEEDED');
    expect(el.text()).toContain('call_1');
  });

  it('"历史"按钮用完整 subId 进入子会话（不截断）', async () => {
    const w = await mountView(pinia);
    const btns = w.findAll('[data-testid="agent-sub-open-history"]');
    expect(btns.length).toBe(1);
    await btns[0].trigger('click');
    await flushPromises();
    expect(enterSubMock).toHaveBeenCalledTimes(1);
    expect(enterSubMock.mock.calls[0][2]).toBe(FULL_SUB_ID);
  });

  it('装配 5 张独占卡片，不存在模块内二级导航', async () => {
    const w = await mountView(pinia);
    expect(w.find('[role="tablist"]').exists()).toBe(false);
    expect(w.find('[data-testid="agent-section-nav"]').exists()).toBe(false);
    for (const id of [
      'agent-card-timeout',
      'agent-card-iterations',
      'agent-card-subagents',
      'agent-card-monitor',
      'agent-card-rejection',
    ]) {
      expect(w.find(`[data-testid="${id}"]`).exists(), id).toBe(true);
    }
    for (const id of ['timeout', 'iterations', 'subagents']) {
      expect(w.get(`[data-testid="agent-card-${id}"] .agent-card-toggle`).attributes('aria-expanded')).toBe('true');
    }
    for (const id of ['monitor', 'rejection']) {
      expect(w.get(`[data-testid="agent-card-${id}"] .agent-card-toggle`).attributes('aria-expanded')).toBe('false');
    }
  });

  it('修改超时只标记超时卡片及其字段行', async () => {
    const w = await mountView(pinia);
    await w.get('[data-testid="agent-timeout-default-input"]').findComponent({ name: 'InputNumber' }).vm.$emit('update:value', 45000);
    await flushPromises();
    expect(w.get('[data-testid="agent-card-timeout"]').find('[data-testid="agent-card-dirty-timeout"]').exists()).toBe(true);
    expect(w.get('[data-testid="agent-card-timeout"]').find('.agent-setting-row.is-changed').exists()).toBe(true);
    expect(w.get('[data-testid="agent-card-iterations"]').find('.agent-card-dirty').exists()).toBe(false);
    expect(w.get('[data-testid="agent-card-subagents"]').find('.agent-card-dirty').exists()).toBe(false);
  });

  it('保存成功后脏态清零', async () => {
    bridgeStub.saveAgentConfig.mockReset().mockResolvedValue({ ok: true, data: structuredClone(agentConfigDoc) });
    const w = await mountView(pinia);
    await w.get('[data-testid="agent-timeout-default-input"]').findComponent({ name: 'InputNumber' }).vm.$emit('update:value', 45000);
    await w.get('[data-testid="agent-save"]').trigger('click');
    await flushPromises();
    expect((w.get('[data-testid="agent-save"]').element as HTMLButtonElement).disabled).toBe(true);
    expect(w.find('[data-testid="agent-card-timeout"] .agent-card-dirty').exists()).toBe(false);
  });

  it('放弃更改恢复到最后一次保存值', async () => {
    const w = await mountView(pinia);
    const store = useAgentConfigStore();
    await w.get('[data-testid="agent-timeout-default-input"]').findComponent({ name: 'InputNumber' }).vm.$emit('update:value', 45000);
    expect(store.config?.execDefaultTimeoutMs).toBe(45000);
    await w.get('[data-testid="agent-discard"]').trigger('click');
    await flushPromises();
    expect(store.config?.execDefaultTimeoutMs).toBe(120000);
  });

  it('恢复全部走确认弹窗并调用后端 resetAll', async () => {
    bridgeStub.resetAgentConfig.mockReset().mockResolvedValue({ ok: true, data: structuredClone(agentConfigDoc) });
    dialogStub.warning.mockImplementation((options: { onPositiveClick?: () => Promise<void> }) => {
      void options.onPositiveClick?.();
    });
    const w = await mountView(pinia);
    await w.get('[data-testid="agent-reset-all"]').trigger('click');
    await flushPromises();
    expect(bridgeStub.resetAgentConfig).toHaveBeenCalledWith('');
  });

  it('整卡恢复按后端别名串行调用', async () => {
    bridgeStub.resetAgentConfig.mockReset().mockResolvedValue({ ok: true, data: structuredClone(agentConfigDoc) });
    const w = await mountView(pinia);
    const card = w.get('[data-testid="agent-card-timeout"]');
    const button = card.findAll('button').find((item) => item.text().includes('恢复本卡默认'));
    expect(button).toBeDefined();
    await button?.trigger('click');
    await flushPromises();
    expect(bridgeStub.resetAgentConfig.mock.calls.map((call) => call[0])).toEqual(['defaultTimeout', 'maxTimeout']);
  });

  it('前端校验失败时展开卡片并展示定位提示', async () => {
    bridgeStub.getAgentConfig.mockReset().mockResolvedValue({
      ok: true,
      data: { ...agentConfigDoc, execDefaultTimeoutMs: 3600001 },
    });
    const w = await mountView(pinia);
    await w.get('[data-testid="agent-timeout-max-input"]').findComponent({ name: 'InputNumber' }).vm.$emit('update:value', 500000);
    const toggle = w.get('[data-testid="agent-card-timeout"] .agent-card-toggle');
    await toggle.trigger('click');
    expect(toggle.attributes('aria-expanded')).toBe('false');
    await w.get('[data-testid="agent-save"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-testid="agent-validation-alert"]').exists()).toBe(true);
    expect(w.get('[data-testid="agent-card-timeout"] .agent-card-toggle').attributes('aria-expanded')).toBe('true');
  });

  it('后端校验失败按 lastError.field 定位到子代理卡片', async () => {
    bridgeStub.saveAgentConfig.mockReset().mockResolvedValue({
      ok: false,
      code: 'AGENT_VALIDATION',
      message: 'invalid subMaxParallel',
    });
    const w = await mountView(pinia);
    await w.get('[data-testid="agent-sub-parallel-input"]').findComponent({ name: 'InputNumber' }).vm.$emit('update:value', 5);
    await w.get('[data-testid="agent-save"]').trigger('click');
    await flushPromises();
    expect(w.get('[data-testid="agent-validation-alert"]').text()).toContain('invalid subMaxParallel');
    expect(w.get('[data-testid="agent-card-subagents"] .agent-card-toggle').attributes('aria-expanded')).toBe('true');
  });

  it('外部 storage 变更不覆盖未保存编辑', async () => {
    const w = await mountView(pinia);
    const store = useAgentConfigStore();
    await w.get('[data-testid="agent-timeout-default-input"]').findComponent({ name: 'InputNumber' }).vm.$emit('update:value', 45000);
    haloEventBus.emitLocal('storage:changed', {});
    await flushPromises();
    expect(store.config?.execDefaultTimeoutMs).toBe(45000);
    expect(store.externalChangePending).toBe(true);
  });

  it('查看拒绝告警展开拒绝记录卡', async () => {
    const w = await mountView(pinia);
    expect(w.get('[data-testid="agent-card-rejection"] .agent-card-toggle').attributes('aria-expanded')).toBe('false');
    await w.get('[data-testid="agent-sub-view-rejection"]').trigger('click');
    await flushPromises();
    expect(w.get('[data-testid="agent-card-rejection"] .agent-card-toggle').attributes('aria-expanded')).toBe('true');
  });

  it('生效说明不再常驻，改由保存按钮 title 承载', async () => {
    const w = await mountView(pinia);
    expect(w.text()).not.toContain('生效范围：全局 config/agent.json');
    expect(w.get('[data-testid="agent-save"]').attributes('title')).toContain('下一轮对话起生效');
  });

  it('运行监控只保留正文一个刷新入口', async () => {
    const w = await mountView(pinia);
    const card = w.get('[data-testid="agent-card-monitor"]');
    expect(card.findAll('button').filter((btn) => btn.text().trim() === '刷新')).toHaveLength(1);
    expect(w.findAll('[data-testid="agent-sub-refresh"]')).toHaveLength(1);
  });
});
