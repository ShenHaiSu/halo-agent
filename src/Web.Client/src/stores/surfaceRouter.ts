// Single switch surface for frame vs legacy rendering.
// One place decides which area reads from the frame boundary; S8 only flips
// defaults here, never scattered checks across the codebase.

export type Surface = 'history' | 'turn' | 'tool' | 'usage' | 'settings';
export type SurfaceMode = 'frame' | 'legacy';

export const SURFACES: readonly Surface[] = ['history', 'turn', 'tool', 'usage', 'settings'];

export interface SurfaceEntry {
  mode: SurfaceMode;
  implemented: boolean;
  owner: string;
}

// Owner records which station is allowed to flip the entry to true.
// Review rule: a diff touching another owner's row is rejected.
export const SURFACE_TABLE: Record<Surface, SurfaceEntry> = {
  history: { mode: 'legacy', implemented: false, owner: 'S3' },
  turn: { mode: 'legacy', implemented: false, owner: 'S6' },
  tool: { mode: 'legacy', implemented: false, owner: 'S6' },
  usage: { mode: 'legacy', implemented: false, owner: 'S7b' },
  settings: { mode: 'legacy', implemented: false, owner: 'S7b' },
};

export const SURFACE_OVERRIDE_KEY = 'halo-surface-modes';

/**
 * Pure resolver with no window dependency so the full combination matrix
 * stays testable in isolation. A surface that is not implemented always
 * resolves to legacy, keeping mixed states safe.
 */
export function resolveMode(surface: Surface, override?: readonly Surface[]): SurfaceMode {
  const entry = SURFACE_TABLE[surface];
  if (!entry.implemented) return 'legacy';
  return override?.includes(surface) ? 'frame' : entry.mode;
}

export interface SurfaceStorageLike {
  getItem(key: string): string | null;
}

function resolveStorage(explicit?: SurfaceStorageLike | null): SurfaceStorageLike | null {
  if (explicit !== undefined) return explicit;
  try {
    if (typeof window !== 'undefined' && window.localStorage) {
      return window.localStorage as SurfaceStorageLike;
    }
  } catch {
    return null;
  }
  return null;
}

function parseOverrideList(raw: string | null): Surface[] | undefined {
  if (!raw) return undefined;
  const wanted = raw
    .split(',')
    .map((part) => part.trim().toLowerCase())
    .filter((part) => part !== '');
  const picked = wanted.filter((part) => (SURFACES as readonly string[]).includes(part)) as Surface[];
  return picked.length > 0 ? picked : undefined;
}

/**
 * Development-only override reader.
 * Reads localStorage plus an optional query string (?surface=history,turn).
 * Storage failures fall back to undefined (defaults), never breaking the chain.
 * Production callers must not invoke this; the build assertion guards the bundle.
 */
export function readOverride(storage?: SurfaceStorageLike | null, search?: string, dev?: boolean): readonly Surface[] | undefined {
  const isDev =
    dev ??
    ((): boolean => {
      try {
        return (import.meta as unknown as { env?: { DEV?: boolean } }).env?.DEV === true;
      } catch {
        return false;
      }
    })();
  if (!isDev) return undefined;
  try {
    const store = resolveStorage(storage);
    const fromStorage = store ? parseOverrideList(store.getItem(SURFACE_OVERRIDE_KEY)) : undefined;
    let fromQuery: Surface[] | undefined;
    try {
      const query = search ?? (typeof window !== 'undefined' ? window.location.search : '');
      if (query) {
        const params = new URLSearchParams(query);
        const single = params.get('surface');
        if (single) fromQuery = parseOverrideList(single);
      }
    } catch {
      fromQuery = undefined;
    }
    const merged: Surface[] = [...(fromStorage ?? []), ...(fromQuery ?? [])];
    const deduped = [...new Set(merged)];
    return deduped.length > 0 ? deduped : undefined;
  } catch {
    return undefined;
  }
}
