import { describe, expect, it } from 'vitest';
import { formatDuration, formatElapsed, formatPermissionSummary, formatRejectionTime } from './agentBehaviorFormat';

describe('formatDuration', () => {
  it.each([
    [0, '不限'],
    [1_000, '约 1 秒'],
    [30_000, '约 30 秒'],
    [60_000, '约 1 分钟'],
    [300_000, '约 5 分钟'],
    [600_000, '约 10 分钟'],
    [3_600_000, '约 1 小时'],
    [59_999, '约 60 秒'],
    [3_599_999, '约 60 分钟'],
    [7_200_000, '约 2 小时'],
  ])('%i 毫秒格式化为 %s', (ms, expected) => {
    expect(formatDuration(ms)).toBe(expected);
  });
});

describe('formatElapsed', () => {
  it.each([
    [0, '00:00'],
    [128_400, '02:08'],
    [246_800, '04:06'],
    [59_999, '00:59'],
    [60_000, '01:00'],
    [3_600_000, '60:00'],
  ])('%i 毫秒格式化为 %s', (ms, expected) => {
    expect(formatElapsed(ms)).toBe(expected);
  });
});

describe('formatRejectionTime', () => {
  it('合法时间转换成本地时间字符串', () => {
    const iso = '2026-10-21T08:00:00.000Z';
    expect(formatRejectionTime(iso)).toBe(new Date(iso).toLocaleString());
  });

  it('非法时间原样返回', () => {
    expect(formatRejectionTime('not-a-date')).toBe('not-a-date');
  });

  it('空时间返回空串', () => {
    expect(formatRejectionTime('')).toBe('');
  });
});

describe('formatPermissionSummary', () => {
  it.each([
    [{ exec: true, file: true, skill: false, memory: false }, 'exec · file'],
    [{ exec: true, file: true, skill: true, memory: false }, 'exec · file · skill'],
    [{ exec: false, file: false, skill: false, memory: false }, '无写权限'],
    [{ exec: true, file: true, skill: true, memory: true }, 'exec · file · skill · memory'],
    [{ exec: false, file: false, skill: false, memory: true }, 'memory'],
    [{ exec: false, file: true, skill: false, memory: true }, 'file · memory'],
  ])('按固定顺序生成权限摘要 %s', (permissions, expected) => {
    expect(formatPermissionSummary(permissions)).toBe(expected);
  });
});
