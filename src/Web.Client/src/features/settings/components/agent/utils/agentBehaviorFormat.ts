/** 智能体行为页的纯展示格式化函数。 */

export function formatDuration(ms: number): string {
  if (ms === 0) return '不限';
  if (ms < 60_000) return `约 ${Math.round(ms / 1_000)} 秒`;
  if (ms < 3_600_000) return `约 ${Math.round(ms / 60_000)} 分钟`;
  return `约 ${Math.round(ms / 3_600_000)} 小时`;
}

export function formatElapsed(ms: number): string {
  const totalSeconds = Math.floor(ms / 1_000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
}

export function formatRejectionTime(iso: string): string {
  if (!iso) return '';
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}

export function formatPermissionSummary(p: { exec: boolean; file: boolean; skill: boolean; memory: boolean }): string {
  const permissions: string[] = [];
  if (p.exec) permissions.push('exec');
  if (p.file) permissions.push('file');
  if (p.skill) permissions.push('skill');
  if (p.memory) permissions.push('memory');
  return permissions.length > 0 ? permissions.join(' · ') : '无写权限';
}
