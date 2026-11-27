/** 智能体行为配置状态回归：保存基线、字段脏态、放弃更改与外部变更保护。 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import type { AgentConfigDocument } from '@shared/bridge/hostBridge';

const { bridgeStub, eventBusStub } = vi.hoisted(() => ({
  bridgeStub: {
    getAgentConfig: vi.fn(),
    saveAgentConfig: vi.fn(),
    resetAgentConfig: vi.fn(),
  },
  eventBusStub: {
    on: vi.fn(),
  },
}));

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...actual, getHostBridge: () => bridgeStub, haloEventBus: eventBusStub };
});

import { useAgentConfigStore } from './agentConfigStore';

function createConfig(overrides: Partial<AgentConfigDocument> = {}): AgentConfigDocument {
  return {
    version: 1,
    execDefaultTimeoutMs: 30_000,
    execMaxTimeoutMs: 300_000,
    maxIterations: 25,
    turnTimeoutMs: 600_000,
    subAgentEnabled: true,
    subMaxParallel: 3,
    subMaxIterations: 15,
    subAllowExecCommand: true,
    subAllowFileWrite: true,
    subAllowSkillWrite: false,
    subAllowMemoryWrite: false,
    subAllowAskUser: false,
    toolsAllowlist: ['read_file'],
    toolsDenylist: [],
    allowedCommands: [],
    scope: 'global',
    updatedAtUtc: '2026-11-25T00:00:00.000Z',
    ...overrides,
  };
}

type AgentConfigStore = ReturnType<typeof useAgentConfigStore>;
let storageChanged: (() => void) | null;

function seedSavedStore(overrides: Partial<AgentConfigDocument> = {}): AgentConfigStore {
  const store = useAgentConfigStore();
  store.config = createConfig(overrides);
  store.markSaved();
  return store;
}
function requireConfig(store: AgentConfigStore): AgentConfigDocument {
  if (!store.config) throw new Error('配置尚未加载');
  return store.config;
}

function requireSavedConfig(store: AgentConfigStore): AgentConfigDocument {
  if (!store.savedConfig) throw new Error('尚无保存基线');
  return store.savedConfig;
}

function emitStorageChanged(): void {
  if (!storageChanged) throw new Error('尚未监听存储变更');
  storageChanged();
}

function expectDirtyConsistency(store: AgentConfigStore): void {
  expect(store.dirty).toBe(store.dirtyFields.length > 0);
}

beforeEach(() => {
  setActivePinia(createPinia());
  vi.resetAllMocks();
  storageChanged = null;
  eventBusStub.on.mockImplementation((eventType: string, handler: (payload: unknown, timestamp: number) => void) => {
    if (eventType === 'storage:changed') storageChanged = () => handler(null, 0);
    return vi.fn();
  });
  bridgeStub.getAgentConfig.mockResolvedValue({ ok: true, data: createConfig() });
  bridgeStub.saveAgentConfig.mockImplementation(async (config: AgentConfigDocument) => ({ ok: true, data: createConfig(config) }));
  bridgeStub.resetAgentConfig.mockResolvedValue({ ok: true, data: createConfig() });
});

describe('agentConfigStore 保存基线与脏字段', () => {
  it('初始状态没有保存基线和脏字段', () => {
    const store = useAgentConfigStore();

    expect(store.config).toBeNull();
    expect(store.savedConfig).toBeNull();
    expect(store.dirtyFields).toEqual([]);
    expect(store.dirty).toBe(false);
    expect(store.externalChangePending).toBe(false);
    expect(store.lastSavedHash).toBe('');
  });

  it('逐字段比较标量与嵌套字段，并包含仅在保存基线存在的字段', () => {
    const store = seedSavedStore({ toolsAllowlist: ['read_file'], fallbackReason: 'saved' });
    const config = requireConfig(store);

    config.maxIterations = 26;
    config.turnTimeoutMs = 0;
    config.toolsAllowlist?.push('write_file');
    delete config.fallbackReason;

    expect(store.dirtyFields).toEqual(['maxIterations', 'turnTimeoutMs', 'toolsAllowlist', 'fallbackReason']);
    expectDirtyConsistency(store);

    config.maxIterations = 25;
    config.turnTimeoutMs = 600_000;
    config.toolsAllowlist = ['read_file'];
    config.fallbackReason = 'saved';

    expect(store.dirtyFields).toEqual([]);
    expectDirtyConsistency(store);
  });

  it('加载成功后深克隆保存基线，编辑数组不会反向污染基线', async () => {
    const loaded = createConfig({ toolsAllowlist: ['read_file'], toolsDenylist: ['danger'] });
    const expectedSaved = createConfig({ toolsAllowlist: ['read_file'], toolsDenylist: ['danger'] });
    bridgeStub.getAgentConfig.mockResolvedValue({ ok: true, data: loaded });
    const store = useAgentConfigStore();
    store.lastError = { field: 'maxIterations', message: '旧错误' };
    store.externalChangePending = true;

    await store.loadConfig();

    expect(store.config).toEqual(loaded);
    expect(store.savedConfig).toEqual(expectedSaved);
    expect(store.savedConfig).not.toBe(store.config);
    expect(store.savedConfig?.toolsAllowlist).not.toBe(store.config?.toolsAllowlist);
    expect(store.lastSavedHash).toBe(JSON.stringify(expectedSaved));
    expect(store.lastError).toBeNull();
    expect(store.externalChangePending).toBe(false);
    expectDirtyConsistency(store);

    const config = requireConfig(store);
    config.toolsAllowlist?.push('write_file');
    config.toolsDenylist?.push('other');

    expect(store.savedConfig?.toolsAllowlist).toEqual(['read_file']);
    expect(store.savedConfig?.toolsDenylist).toEqual(['danger']);
    expect(store.dirtyFields).toEqual(['toolsAllowlist', 'toolsDenylist']);
    expectDirtyConsistency(store);
  });

  it('放弃更改恢复保存基线克隆，并清除错误与外部变更标记', () => {
    const store = seedSavedStore({ toolsAllowlist: ['read_file'], fallbackReason: 'saved' });
    const expected = createConfig({ toolsAllowlist: ['read_file'], fallbackReason: 'saved' });
    const config = requireConfig(store);
    config.maxIterations = 99;
    config.toolsAllowlist?.push('write_file');
    delete config.fallbackReason;
    store.lastError = { field: 'maxIterations', message: '校验失败' };
    store.externalChangePending = true;

    store.discardChanges();

    const discardedConfig = requireConfig(store);
    const savedConfig = requireSavedConfig(store);
    expect(discardedConfig).toEqual(expected);
    expect(savedConfig).toEqual(expected);
    expect(discardedConfig).not.toBe(savedConfig);
    expect(discardedConfig.toolsAllowlist).not.toBe(savedConfig.toolsAllowlist);
    expect(store.lastError).toBeNull();
    expect(store.externalChangePending).toBe(false);
    expectDirtyConsistency(store);

    discardedConfig.toolsAllowlist?.push('write_file');
    expect(savedConfig.toolsAllowlist).toEqual(['read_file']);
    expect(store.dirtyFields).toEqual(['toolsAllowlist']);
  });

  it('保存成功以后端返回值为真相源并更新保存基线', async () => {
    const store = seedSavedStore();
    const config = requireConfig(store);
    config.maxIterations = 40;
    store.lastError = { field: 'maxIterations', message: '旧错误' };
    store.externalChangePending = true;
    const serverConfig = createConfig({ maxIterations: 41, updatedAtUtc: '2026-11-25T01:00:00.000Z' });
    bridgeStub.saveAgentConfig.mockResolvedValue({ ok: true, data: serverConfig });

    const response = await store.saveConfig();

    expect(response.ok).toBe(true);
    expect(bridgeStub.saveAgentConfig).toHaveBeenCalledWith(expect.objectContaining({ maxIterations: 40 }));
    expect(store.config).toEqual(serverConfig);
    expect(store.savedConfig).toEqual(serverConfig);
    expect(store.savedConfig).not.toBe(store.config);
    expect(store.lastSavedHash).toBe(JSON.stringify(serverConfig));
    expect(store.lastError).toBeNull();
    expect(store.externalChangePending).toBe(false);
    expectDirtyConsistency(store);
  });

  it('单项与全部恢复成功都立即更新保存基线', async () => {
    const store = seedSavedStore();
    const config = requireConfig(store);
    config.subMaxParallel = 8;
    const fieldConfig = createConfig({ subMaxParallel: 4 });
    const allConfig = createConfig({ subMaxParallel: 3, subMaxIterations: 15, subAllowSkillWrite: false });
    bridgeStub.resetAgentConfig.mockResolvedValueOnce({ ok: true, data: fieldConfig }).mockResolvedValueOnce({ ok: true, data: allConfig });

    await store.resetField('subParallel');

    expect(bridgeStub.resetAgentConfig).toHaveBeenNthCalledWith(1, 'subParallel');
    expect(store.config).toEqual(fieldConfig);
    expect(store.savedConfig).toEqual(fieldConfig);
    expectDirtyConsistency(store);

    requireConfig(store).subMaxParallel = 8;
    store.lastError = { field: 'subMaxParallel', message: '旧错误' };
    store.externalChangePending = true;
    await store.resetAll();

    expect(bridgeStub.resetAgentConfig).toHaveBeenNthCalledWith(2, '');
    expect(store.config).toEqual(allConfig);
    expect(store.savedConfig).toEqual(allConfig);
    expect(store.lastError).toBeNull();
    expect(store.externalChangePending).toBe(false);
    expectDirtyConsistency(store);
  });

  it('保存失败不更新保存基线，并保留脏字段', async () => {
    const store = seedSavedStore();
    requireConfig(store).maxIterations = 40;
    const savedBefore = store.savedConfig;
    bridgeStub.saveAgentConfig.mockResolvedValue({ ok: false, code: 'AGENT_VALIDATION', message: 'maxIterations 非法' });

    await store.saveConfig();

    expect(store.savedConfig).toBe(savedBefore);
    expect(store.dirtyFields).toEqual(['maxIterations']);
    expect(store.dirty).toBe(true);
    expect(store.lastError).toEqual({ field: 'maxIterations', message: 'maxIterations 非法' });
  });

  it('touchDirty 只清除旧错误，不改变配置脏态', () => {
    const store = seedSavedStore();
    store.lastError = { field: 'maxIterations', message: '旧错误' };

    store.touchDirty();

    expect(store.lastError).toBeNull();
    expect(store.dirtyFields).toEqual([]);
    expect(store.dirty).toBe(false);
  });
});

describe('agentConfigStore 后端错误字段定位', () => {
  it.each([
    ['子代理并发优先', 'subMaxParallel subMaxIterations maxIterations 并发', 'subMaxParallel'],
    ['子代理轮次先于最大轮次', 'subMaxIterations maxIterations 子轮次', 'subMaxIterations'],
    ['子代理总开关先于权限', 'subAgentEnabled subAllowFileWrite 总开关', 'subAgentEnabled'],
    ['子代理权限先于单轮超时', 'subAllowFileWrite turnTimeoutMs 权限', 'subPerms'],
    ['单轮超时先于最大轮次', 'turnTimeoutMs maxIterations 单轮最大时间', 'turnTimeoutMs'],
    ['兼容不带单位后缀的单轮超时消息', 'turnTimeout maxIterations', 'turnTimeoutMs'],
    ['最大轮次先于执行上限', 'maxIterations execMaxTimeoutMs 轮次', 'maxIterations'],
    ['执行上限先于默认超时', 'execMaxTimeoutMs execDefaultTimeoutMs 上限', 'execMaxTimeoutMs'],
    ['未知字段回落到默认超时', '未知字段', 'execDefaultTimeoutMs'],
  ])('%s按既有优先级定位', async (_label, message, expectedField) => {
    const store = seedSavedStore();
    bridgeStub.saveAgentConfig.mockResolvedValue({ ok: false, code: 'AGENT_VALIDATION', message });

    await store.saveConfig();

    expect(store.lastError).toEqual({ field: expectedField, message });
  });
});

describe('agentConfigStore 外部存储变更保护', () => {
  it('有未保存修改时只标记待处理，不请求也不覆盖编辑', async () => {
    const store = seedSavedStore();
    requireConfig(store).maxIterations = 40;

    store.watchStorageChange();
    store.watchStorageChange();
    expect(eventBusStub.on).toHaveBeenCalledTimes(1);
    expect(eventBusStub.on).toHaveBeenCalledWith('storage:changed', expect.any(Function));

    emitStorageChanged();
    await flushPromises();

    expect(bridgeStub.getAgentConfig).not.toHaveBeenCalled();
    expect(store.externalChangePending).toBe(true);
    expect(requireConfig(store).maxIterations).toBe(40);
    expect(store.savedConfig?.maxIterations).toBe(25);
    expect(store.dirtyFields).toEqual(['maxIterations']);
  });

  it('无未保存修改时自动加载外部配置', async () => {
    const store = seedSavedStore();
    const externalConfig = createConfig({ maxIterations: 77 });
    bridgeStub.getAgentConfig.mockResolvedValue({ ok: true, data: externalConfig });
    store.watchStorageChange();

    emitStorageChanged();
    await flushPromises();

    expect(bridgeStub.getAgentConfig).toHaveBeenCalledTimes(1);
    expect(store.config).toEqual(externalConfig);
    expect(store.savedConfig).toEqual(externalConfig);
    expect(store.externalChangePending).toBe(false);
    expectDirtyConsistency(store);
  });

  it('放弃修改并清除待处理标记后，可重新加载外部配置', async () => {
    const store = seedSavedStore();
    requireConfig(store).maxIterations = 40;
    store.watchStorageChange();
    emitStorageChanged();
    expect(store.externalChangePending).toBe(true);

    store.discardChanges();
    expect(store.externalChangePending).toBe(false);
    expect(requireConfig(store).maxIterations).toBe(25);

    const externalConfig = createConfig({ maxIterations: 77 });
    bridgeStub.getAgentConfig.mockResolvedValue({ ok: true, data: externalConfig });
    emitStorageChanged();
    await flushPromises();

    expect(store.config).toEqual(externalConfig);
    expect(store.savedConfig).toEqual(externalConfig);
    expectDirtyConsistency(store);
  });
});
