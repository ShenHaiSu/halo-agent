// src/Web.Client/src/components/layout/engineStatusMeta.test.ts
// P07-Enhance 01：状态→颜色映射 + hover 文案含 pid/reason。
import { describe, expect, it } from 'vitest';
import { describeEngineStatus, mapEngineState } from './engineStatusMeta';

describe('engineStatusMeta', () => {
  it('ready → 绿常亮', () => {
    expect(mapEngineState('ready')).toEqual({ color: 'ready', text: '引擎已连接' });
  });
  it('starting/restarting → 黄脉冲', () => {
    expect(mapEngineState('starting').color).toBe('pending');
    expect(mapEngineState('restarting').color).toBe('pending');
  });
  it('failed/stopped → 红', () => {
    expect(mapEngineState('failed').color).toBe('error');
    expect(mapEngineState('stopped').color).toBe('error');
  });
  it('hover 文案含 pid 与 Mock', () => {
    const s = describeEngineStatus({ state: 'failed', pid: 1234, reason: 'boom' }, true);
    expect(s).toContain('1234');
    expect(s).toContain('Mock');
  });
  it('hover 文案含真实版本态（非 Mock 不带 Mock 字样）', () => {
    const s = describeEngineStatus({ state: 'ready', pid: 42 }, false);
    expect(s).toContain('42');
    expect(s).not.toContain('Mock');
  });
});
