// src/Web.Client/src/utils/format.ts
// D4-8 格式化工具（全部用 Intl，不引 dayjs）。

/** 相对时间：刚刚 / 5 分钟前 / 3 小时前 / 昨天 / 3 天前 / 2025-01-15 */
export function formatRelativeTime(ts: number): string {
  const diff = Date.now() - ts;
  const sec = Math.round(diff / 1000);

  if (sec < 60) return '刚刚';
  const min = Math.round(sec / 60);
  if (min < 60) return `${min} 分钟前`;
  const hour = Math.round(min / 60);
  if (hour < 24) return `${hour} 小时前`;

  const now = new Date();
  const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  if (ts >= startOfToday - 86400_000) return '昨天';

  const days = Math.round((startOfToday - ts) / 86400_000);
  if (days < 7) return `${days} 天前`;

  return formatDate(ts);
}

/** 2025-01-15 */
export function formatDate(ts: number): string {
  const d = new Date(ts);
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, '0');
  const day = String(d.getDate()).padStart(2, '0');
  return `${y}-${m}-${day}`;
}

/** 2025-01-15 14:32 */
export function formatDateTime(ts: number): string {
  const d = new Date(ts);
  const hh = String(d.getHours()).padStart(2, '0');
  const mm = String(d.getMinutes()).padStart(2, '0');
  return `${formatDate(ts)} ${hh}:${mm}`;
}

/** 1.2 KB / 3.4 MB / 1.1 GB */
export function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let v = n / 1024;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toFixed(1)} ${units[i]}`;
}

/** 1234 → 1,234 */
export function formatNumber(n: number): string {
  return new Intl.NumberFormat('zh-CN').format(n);
}

/** 1234 → 1.2k / 1234567 → 1.2M */
export function formatCompactNumber(n: number): string {
  return new Intl.NumberFormat('zh-CN', {
    notation: 'compact',
    maximumFractionDigits: 1,
  }).format(n);
}

/** 毫秒 → 1.2s / 350ms / 2m 5s */
export function formatDuration(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)}ms`;
  const s = ms / 1000;
  if (s < 60) return `${s.toFixed(1)}s`;
  const m = Math.floor(s / 60);
  const rest = Math.round(s % 60);
  return rest === 0 ? `${m}m` : `${m}m ${rest}s`;
}
