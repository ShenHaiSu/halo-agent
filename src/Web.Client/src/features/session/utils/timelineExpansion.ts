const expandedTurns = new Set<string>();

export function setTurnExpanded(turnId: string, expanded: boolean): void {
  const id = String(turnId || '').trim();
  if (!id) return;
  if (expanded) expandedTurns.add(id);
  else expandedTurns.delete(id);
}

export function expandedBoxCount(): number {
  return expandedTurns.size;
}

export function quotaLimit(): number {
  const q = 600 + 64 * expandedTurns.size;
  return q > 2000 ? 2000 : q;
}

export function clearExpansion(): void {
  expandedTurns.clear();
}
