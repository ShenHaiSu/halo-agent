// src/Web.Client/src/components/usage/usagePresets.ts
// P16 用量筛选：8 预设精确语义（本地日历 → UTC ms，前端换算）。

export type UsagePresetKey =
  | 'today'
  | 'yesterday'
  | 'last3'
  | 'last7'
  | 'last30'
  | 'week'
  | 'month'
  | 'all';

export interface UsageRange {
  fromMs?: number;
  toMs?: number;
}

export const USAGE_PRESETS: Array<{ key: UsagePresetKey; label: string; title?: string }> = [
  { key: 'today', label: '今日' },
  { key: 'yesterday', label: '昨日' },
  { key: 'last3', label: '近三日' },
  { key: 'last7', label: '近七日' },
  { key: 'last30', label: '近三十日' },
  { key: 'week', label: '本周' },
  { key: 'month', label: '本月' },
  { key: 'all', label: '全部时间', title: '仅重置日期，保留 provider/model/session' },
];

const DAY = 86400000;

/** 本地 00:00 时间戳。 */
export function startOfTodayMs(now: Date = new Date()): number {
  return new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
}

/** 预设 → [fromMs, toMs]（本地日历换算 UTC ms；全部时间返回空）。 */
export function getPresetRange(key: UsagePresetKey, now: Date = new Date()): UsageRange {
  const start = startOfTodayMs(now);
  const t = now.getTime();
  switch (key) {
    case 'today':
      return { fromMs: start, toMs: start + DAY - 1 };
    case 'yesterday':
      return { fromMs: start - DAY, toMs: start - 1 };
    case 'last3':
      return { fromMs: start - 2 * DAY, toMs: t };
    case 'last7':
      return { fromMs: start - 6 * DAY, toMs: t };
    case 'last30':
      return { fromMs: start - 29 * DAY, toMs: t };
    case 'week': {
      // getDay()：周日=0 → -6 天，其余 -(day-1)
      const day = now.getDay();
      const diff = day === 0 ? 6 : day - 1;
      return { fromMs: start - diff * DAY, toMs: t };
    }
    case 'month':
      return { fromMs: new Date(now.getFullYear(), now.getMonth(), 1).getTime(), toMs: t };
    case 'all':
      return {};
  }
}

/** 清理用：N 天前本地 00:00 → UTC ms。 */
export function daysAgoMidnightMs(days: number, now: Date = new Date()): number {
  return startOfTodayMs(now) - days * DAY;
}

/** 本地毫秒 → datetime-local 输入值（YYYY-MM-DDTHH:mm）。 */
export function toInputValue(ms?: number): string {
  if (ms === undefined || ms === null) return '';
  const d = new Date(ms);
  const pad = (n: number): string => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** datetime-local 输入值 → 本地毫秒（空串返回 undefined）。 */
export function fromInputValue(s: string): number | undefined {
  const t = s.trim();
  if (!t) return undefined;
  const ms = new Date(t).getTime();
  return Number.isNaN(ms) ? undefined : ms;
}
