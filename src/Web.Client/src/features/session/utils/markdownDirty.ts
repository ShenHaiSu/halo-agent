const dirtyKeys = new Set<string>();

export function markBlockDirty(key: string): void {
  const k = String(key || '').trim();
  if (k) dirtyKeys.add(k);
}

export function markBlockClean(key: string): void {
  const k = String(key || '').trim();
  if (k) dirtyKeys.delete(k);
}

export function isBlockDirty(key: string): boolean {
  return dirtyKeys.has(String(key || '').trim());
}

export function dirtyCount(): number {
  return dirtyKeys.size;
}
