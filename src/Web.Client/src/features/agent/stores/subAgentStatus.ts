/** 子代理状态聚合的共享纯函数：卡片 store 与轮次 store 共用一份终态口径，避免两处漂移。 */

export interface SubAgentItemLike {
  subId: string;
  label: string;
  status: string;
}

export interface SubAgentsLike {
  total: number;
  done: number;
  waiting: boolean;
  items: SubAgentItemLike[];
}

/** 只有进入这些状态的子项才计入 done。 */
export const TERMINAL = new Set(['succeeded', 'failed', 'cancelled', 'timedOut', 'truncated']);

/** 把后端子项 payload 映射为前端统一终态语义：超时/取消/截断优先于 success 布尔。 */
export function mapSubStatus(s: Record<string, unknown>): string {
  if (s['timedOut'] === true) return 'timedOut';
  const code = String(s['errorCode'] ?? '');
  if (code === 'SUBAGENT_CANCELLED') return 'cancelled';
  if (code === 'TURN_TRUNCATED') return 'truncated';
  return (s['success'] as boolean) ? 'succeeded' : 'failed';
}

/** 统计已到终态的子项数量（唯一口径，Started / Progress / Released / 兜底共用）。 */
export function countTerminalItems(items: ReadonlyArray<{ status: string }>): number {
  return items.filter((i) => TERMINAL.has(i.status)).length;
}

/**
 * 主轮终态兜底收尾：只把仍为 running 的子项收敛到 fallback，
 * 绝不覆盖兄弟子已有的真实终态；随后解除 waiting 并按终态重算 done。
 */
export function finalizeSubAgents(t: { subAgents: SubAgentsLike }, fallback: 'cancelled' | 'failed'): void {
  for (const item of t.subAgents.items) {
    if (item.status === 'running') item.status = fallback;
  }
  t.subAgents.waiting = false;
  t.subAgents.done = countTerminalItems(t.subAgents.items);
}
