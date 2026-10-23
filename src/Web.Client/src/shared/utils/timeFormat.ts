/**
 * 会话卡时间格式化（need02-03）：纯函数、无第三方依赖。
 * 所有展示均为本地时区；`createdAtUtc / updatedAtUtc` 为 UTC ISO 字符串，
 * `new Date(iso)` 会自动转换为本地时间展示。
 */

export interface SessionTimeSource {
  createdAtUtc: string;
  updatedAtUtc: string;
  messageCount: number;
}

function pad2(n: number): string {
  return String(n).padStart(2, '0');
}

/** 本地时间全量：`2026-10-14 21:10:44`，非法输入返回 `-`。 */
export function toLocale(iso: string): string {
  const d = new Date(iso);
  if (isNaN(+d)) return '-';
  return (
    `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())} ` +
    `${pad2(d.getHours())}:${pad2(d.getMinutes())}:${pad2(d.getSeconds())}`
  );
}

/** 卡面创建时间：同年 `MM-DD`，跨年 `YYYY-MM-DD`，非法输入返回 `-`。 */
export function formatSessionTime(iso: string): string {
  const d = new Date(iso);
  if (isNaN(+d)) return '-';
  const now = new Date();
  const sameYear = d.getFullYear() === now.getFullYear();
  const md = `${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`;
  return sameYear ? md : `${d.getFullYear()}-${md}`;
}

/** 卡面最后活跃相对时间：刚刚 / N分钟前 / N小时前 / N天前，超 7 天回退绝对日期。 */
export function formatRelativeTime(iso: string): string {
  const t = new Date(iso).getTime();
  if (isNaN(t)) return '-';
  const s = Math.max(0, (Date.now() - t) / 1000);
  if (s < 60) return '刚刚';
  if (s < 3600) return `${Math.floor(s / 60)}分钟前`;
  if (s < 86400) return `${Math.floor(s / 3600)}小时前`;
  if (s < 86400 * 7) return `${Math.floor(s / 86400)}天前`;
  return formatSessionTime(iso);
}

/** need04-01：坏时间判定：空串/null/Invalid Date/年<2000（含0001）/无时区解析失败统一收敛。 */
export function isBadTime(iso: unknown): boolean {
  if (typeof iso !== 'string' || !iso.trim()) return true;
  const d = new Date(iso);
  if (isNaN(+d)) return true;
  if (d.getFullYear() < 2000) return true;
  return false;
}

/** 卡面第二行 tooltip 全量：创建 / 最后消息（本地时间）/ 消息条数。 */
export function sessionTimeTitle(s: SessionTimeSource): string {
  const bad = isBadTime(s.createdAtUtc) && isBadTime(s.updatedAtUtc);
  if (bad) return `创建时间未知（老数据已自动修复） · 共 ${s.messageCount} 条消息`;
  return `创建于 ${toLocale(s.createdAtUtc)} · 最后消息 ${toLocale(s.updatedAtUtc)} · 共 ${s.messageCount} 条消息`;
}
