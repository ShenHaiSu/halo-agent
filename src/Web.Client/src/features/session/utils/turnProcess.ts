/** bigChange P2-6: Turn four-state machine + running copy. */
export type TurnPhase = 'failed' | 'stopped' | 'running' | 'completed';

export interface TurnPhaseInput {
  hasOwnError: boolean;
  stopped: boolean;
  isActive: boolean;
  hasContent: boolean;
}

export function turnPhaseOf(input: TurnPhaseInput): TurnPhase {
  if (input.hasOwnError) return 'failed';
  if (input.stopped) return 'stopped';
  if (input.isActive) return 'running';
  return 'completed';
}

export type RunningSubPhase = 'compacting' | 'recovering' | 'retrying' | 'waiting-subagents' | 'thinking' | 'working';

export function runningCopy(sub: RunningSubPhase, toolCount: number, iteration: number): string {
  const base = `思考中… · 已调用 ${toolCount} 个工具 · 第 ${iteration} 轮`;
  switch (sub) {
    case 'compacting':
      return `压缩中… · 第 ${iteration} 轮`;
    case 'recovering':
      return `恢复中… · 第 ${iteration} 轮`;
    case 'retrying':
      return `重试中… · 第 ${iteration} 轮`;
    case 'waiting-subagents':
      return `等待子代理… · 第 ${iteration} 轮`;
    case 'thinking':
      return base;
    case 'working':
      return `执行中… · 已调用 ${toolCount} 个工具 · 第 ${iteration} 轮`;
  }
}

export function turnDurationLabel(startedAt: number | null, now = Date.now()): string {
  if (!startedAt || startedAt <= 0) return '';
  const s = Math.max(0, Math.round((now - startedAt) / 1000));
  if (s <= 0) return '';
  return `For ${s}s`;
}
