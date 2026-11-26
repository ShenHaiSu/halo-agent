export function isCompactTurn(turn: { kind?: string | null }): boolean {
  return String(turn.kind || '') === 'compact';
}

export function compactTurnLabel(removedMessages: number): string {
  const count = Math.max(0, Number(removedMessages) || 0);
  return count > 0 ? `上下文压缩 · ${count} 条 → 摘要` : '上下文压缩';
}
