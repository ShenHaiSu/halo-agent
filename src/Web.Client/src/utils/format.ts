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

/** P17-03 中文数量单位：千 / 万 / 亿 / 兆（1 兆 = 10^12，再往上不做）。
 *
 * 档位（舍入溢出进位）：n<1000 → formatNumber 精确；1000≤n<1万 → 千；
 * 1万≤n<1亿 → 万；1亿≤n<1e12 → 亿；≥1e12 → 兆。保留 1 位去尾零，
 * 档内舍入后触及上档（千≥10 / 万≥1000 / 亿≥10000）即升档重算。
 * 轴 tick 可为浮点：先 Math.round 再进档。非有限数 → —；负数 → - + 绝对值。
 */
export function formatZhCount(n: number): string {
  if (!Number.isFinite(n)) return '—';
  if (n < 0) return `-${formatZhCount(-n)}`;
  const v = Math.round(n);
  if (v < 1000) return formatNumber(v);
  const units: Array<{ limit: number; div: number; suffix: string; spill: number }> = [
    { limit: 1e4, div: 1e3, suffix: '千', spill: 10 },
    { limit: 1e8, div: 1e4, suffix: '万', spill: 1000 },
    { limit: 1e12, div: 1e8, suffix: '亿', spill: 10000 },
    { limit: Infinity, div: 1e12, suffix: '兆', spill: Infinity },
  ];
  let idx = units.findIndex((u) => v < u.limit);
  if (idx < 0) idx = units.length - 1;
  while (idx < units.length) {
    const u = units[idx]!;
    const r = Math.round((v / u.div) * 10) / 10;
    if (r < u.spill) return `${Number.isInteger(r) ? String(r) : r.toFixed(1)}${u.suffix}`;
    idx++;
  }
  return formatNumber(v);
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
