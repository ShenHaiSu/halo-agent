// src/Web.Client/src/components/usage/usagePresets.test.ts
// P16：8 预设期望值表（周日/月初一/跨月/闰日）。
import { describe, expect, it } from 'vitest';
import { daysAgoMidnightMs, getPresetRange, startOfTodayMs } from './usagePresets';

const DAY = 86400000;

describe('usagePresets', () => {
  it('今日/昨日语义', () => {
    const now = new Date(2026, 9, 30, 15, 30); // 2026-10-30 周五
    const start = new Date(2026, 9, 30).getTime();
    expect(getPresetRange('today', now)).toEqual({ fromMs: start, toMs: start + DAY - 1 });
    expect(getPresetRange('yesterday', now)).toEqual({ fromMs: start - DAY, toMs: start - 1 });
  });

  it('近三/七/三十日语义', () => {
    const now = new Date(2026, 9, 30, 15, 30);
    const start = new Date(2026, 9, 30).getTime();
    expect(getPresetRange('last3', now)).toEqual({ fromMs: start - 2 * DAY, toMs: now.getTime() });
    expect(getPresetRange('last7', now)).toEqual({ fromMs: start - 6 * DAY, toMs: now.getTime() });
    expect(getPresetRange('last30', now)).toEqual({ fromMs: start - 29 * DAY, toMs: now.getTime() });
  });

  it('本周：周日回退 6 天', () => {
    // 2026-11-01 是周日
    const sunday = new Date(2026, 10, 1, 12, 0);
    expect(sunday.getDay()).toBe(0);
    const r = getPresetRange('week', sunday);
    // 本周一 2026-10-26 00:00
    expect(r.fromMs).toBe(new Date(2026, 9, 26).getTime());
    expect(r.toMs).toBe(sunday.getTime());
  });

  it('本周：周三回退 2 天', () => {
    const wed = new Date(2026, 9, 28, 12, 0); // 周三
    expect(wed.getDay()).toBe(3);
    const r = getPresetRange('week', wed);
    expect(r.fromMs).toBe(new Date(2026, 9, 26).getTime());
  });

  it('本月：月初一 from=1 日 00:00', () => {
    const first = new Date(2026, 10, 1, 8, 0); // 11 月 1 日
    const r = getPresetRange('month', first);
    expect(r.fromMs).toBe(new Date(2026, 10, 1).getTime());
  });

  it('跨月：近七日可跨到上月', () => {
    const now = new Date(2026, 10, 2, 10, 0); // 11 月 2 日
    const r = getPresetRange('last7', now);
    // 10 月 27 日
    expect(r.fromMs).toBe(new Date(2026, 9, 27).getTime());
  });

  it('闰日：2024-02-29 近三日', () => {
    const leap = new Date(2024, 1, 29, 12, 0);
    const r = getPresetRange('last3', leap);
    expect(r.fromMs).toBe(new Date(2024, 1, 27).getTime());
  });

  it('全部时间只清日期', () => {
    expect(getPresetRange('all', new Date())).toEqual({});
  });

  it('daysAgoMidnightMs：90 天前本地 00:00', () => {
    const now = new Date(2026, 9, 30, 15, 0);
    expect(daysAgoMidnightMs(90, now)).toBe(startOfTodayMs(now) - 90 * DAY);
  });
});
