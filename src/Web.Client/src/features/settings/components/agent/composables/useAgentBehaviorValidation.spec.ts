import { describe, expect, it } from 'vitest';
import type { AgentConfigDocument } from '@shared/bridge/hostBridge';
import { validateAgentBehavior } from './useAgentBehaviorValidation';

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
    scope: 'global',
    updatedAtUtc: '2026-11-25T00:00:00.000Z',
    ...overrides,
  };
}

describe('validateAgentBehavior 合法边界', () => {
  it.each([
    ['默认配置', createConfig()],
    ['默认超时为 0', createConfig({ execDefaultTimeoutMs: 0 })],
    ['默认超时为 1', createConfig({ execDefaultTimeoutMs: 1 })],
    ['默认超时为上限且关闭执行上限', createConfig({ execDefaultTimeoutMs: 3_600_000, execMaxTimeoutMs: 0 })],
    ['执行上限为 0', createConfig({ execMaxTimeoutMs: 0 })],
    ['执行上限为 1 且默认超时为 0', createConfig({ execDefaultTimeoutMs: 0, execMaxTimeoutMs: 1 })],
    ['执行上限为最大允许值', createConfig({ execMaxTimeoutMs: 3_600_000 })],
    ['默认超时等于执行上限', createConfig({ execDefaultTimeoutMs: 300_000, execMaxTimeoutMs: 300_000 })],
    ['最大轮次为 0', createConfig({ maxIterations: 0 })],
    ['最大轮次为 500', createConfig({ maxIterations: 500 })],
    ['单轮超时为 0', createConfig({ turnTimeoutMs: 0 })],
    ['单轮超时为 1000', createConfig({ turnTimeoutMs: 1_000 })],
    ['单轮超时为 600000', createConfig({ turnTimeoutMs: 600_000 })],
    ['旧配置缺少单轮超时', createConfig({ turnTimeoutMs: undefined })],
    ['子代理并发为 1', createConfig({ subMaxParallel: 1 })],
    ['子代理并发为 8', createConfig({ subMaxParallel: 8 })],
    ['子代理轮次为 1', createConfig({ subMaxIterations: 1 })],
    ['子代理轮次为 100', createConfig({ subMaxIterations: 100 })],
  ])('%s合法', (_label, config) => {
    expect(validateAgentBehavior(config)).toEqual([]);
  });
});

describe('validateAgentBehavior 非法边界', () => {
  it.each([
    ['默认超时超过最大值', createConfig({ execDefaultTimeoutMs: 3_600_001 }), 'execDefaultTimeoutMs', 'timeout'],
    ['默认超时为负数', createConfig({ execDefaultTimeoutMs: -1 }), 'execDefaultTimeoutMs', 'timeout'],
    ['默认超时不是有限数', createConfig({ execDefaultTimeoutMs: Number.NaN }), 'execDefaultTimeoutMs', 'timeout'],
    ['执行上限超过最大值', createConfig({ execMaxTimeoutMs: 3_600_001 }), 'execMaxTimeoutMs', 'timeout'],
    ['执行上限为负数', createConfig({ execMaxTimeoutMs: -1 }), 'execMaxTimeoutMs', 'timeout'],
    ['默认超时大于执行上限', createConfig({ execDefaultTimeoutMs: 400_000, execMaxTimeoutMs: 300_000 }), 'execDefaultTimeoutMs', 'timeout'],
    ['最大轮次超过最大值', createConfig({ maxIterations: 501 }), 'maxIterations', 'iterations'],
    ['最大轮次为负数', createConfig({ maxIterations: -1 }), 'maxIterations', 'iterations'],
    ['单轮超时低于最小值', createConfig({ turnTimeoutMs: 999 }), 'turnTimeoutMs', 'iterations'],
    ['单轮超时超过最大值', createConfig({ turnTimeoutMs: 600_001 }), 'turnTimeoutMs', 'iterations'],
    ['单轮超时为负数', createConfig({ turnTimeoutMs: -1 }), 'turnTimeoutMs', 'iterations'],
    ['子代理并发为 0', createConfig({ subMaxParallel: 0 }), 'subMaxParallel', 'subagents'],
    ['子代理并发超过最大值', createConfig({ subMaxParallel: 9 }), 'subMaxParallel', 'subagents'],
    ['子代理轮次为 0', createConfig({ subMaxIterations: 0 }), 'subMaxIterations', 'subagents'],
    ['子代理轮次超过最大值', createConfig({ subMaxIterations: 101 }), 'subMaxIterations', 'subagents'],
  ])('%s非法并映射到正确卡片', (_label, config, key, card) => {
    const issues = validateAgentBehavior(config);

    expect(issues).toHaveLength(1);
    expect(issues[0]).toMatchObject({ key, card });
    expect(issues[0].message).not.toBe('');
  });
});

describe('validateAgentBehavior 错误顺序', () => {
  it('多个范围错误按字段契约顺序稳定返回', () => {
    const issues = validateAgentBehavior(
      createConfig({
        execDefaultTimeoutMs: -1,
        execMaxTimeoutMs: -1,
        maxIterations: 501,
        turnTimeoutMs: 999,
        subMaxParallel: 0,
        subMaxIterations: 101,
      })
    );

    expect(issues.map((issue) => issue.key)).toEqual([
      'execDefaultTimeoutMs',
      'execMaxTimeoutMs',
      'maxIterations',
      'turnTimeoutMs',
      'subMaxParallel',
      'subMaxIterations',
    ]);
    expect(issues.map((issue) => issue.card)).toEqual(['timeout', 'timeout', 'iterations', 'iterations', 'subagents', 'subagents']);
  });

  it('跨字段错误在单项范围错误之后返回，且不重复报默认超时', () => {
    const issues = validateAgentBehavior(
      createConfig({
        execDefaultTimeoutMs: 400_000,
        execMaxTimeoutMs: 300_000,
        turnTimeoutMs: 999,
        subMaxParallel: 0,
      })
    );

    expect(issues.map((issue) => issue.key)).toEqual(['turnTimeoutMs', 'subMaxParallel', 'execDefaultTimeoutMs']);
    expect(issues[2]).toEqual({
      key: 'execDefaultTimeoutMs',
      card: 'timeout',
      message: '默认超时不得大于执行超时上限',
    });
  });
});
