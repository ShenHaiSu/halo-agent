import { setNewEngineEnabled } from './legacy';

/** Cutover batch letters served by the engine chain. */
export const ENGINE_BATCHES: readonly string[] = ['A', 'B', 'C'];

/** Capability probe snapshot shown on the settings self-check. */
export interface ChainProbe {
  batches: string[];
  frameDefault: boolean | null;
  bridgeVersion: number | null;
  contractVersion: string | null;
}

export function emptyChainProbe(): ChainProbe {
  return { batches: [], frameDefault: null, bridgeVersion: null, contractVersion: null };
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null ? (value as Record<string, unknown>) : null;
}

/** Normalize the batch list from a probe payload; unknown entries are dropped. */
export function parseBatchList(raw: unknown): string[] {
  if (!Array.isArray(raw)) return [];
  const picked = raw.filter((entry): entry is string => typeof entry === 'string' && (ENGINE_BATCHES as readonly string[]).includes(entry));
  return [...new Set(picked)];
}

/**
 * Describe a `system.info` probe result for the self-check. Missing fields
 * stay null so the panel can badge them instead of guessing.
 */
export function describeChainProbe(result: Record<string, unknown>): ChainProbe {
  const routing = asRecord(result['routing']);
  const fromRouting = routing ? parseBatchList(routing['enabledBatches']) : [];
  const alias = parseBatchList(result['batches']);
  const batches = [...new Set([...fromRouting, ...alias])];
  const frameRaw = routing ? routing['defaultFrame'] : undefined;
  const bridgeRaw = result['bridgeVersion'];
  const contractRaw = result['contractVersion'];
  return {
    batches,
    frameDefault: typeof frameRaw === 'boolean' ? frameRaw : null,
    bridgeVersion: typeof bridgeRaw === 'number' ? bridgeRaw : null,
    contractVersion: typeof contractRaw === 'string' ? contractRaw : null,
  };
}

/** Batch letters missing from the probe compared with the served set. */
export function missingBatches(probe: ChainProbe): string[] {
  return ENGINE_BATCHES.filter((batch) => !probe.batches.includes(batch));
}

/**
 * One-click rollback to the backup chain: persist the explicit opt-out and
 * hand control to the caller for reload so tests can observe without a page.
 */
export function rollbackToLegacyChain(reload: () => void): void {
  setNewEngineEnabled(false);
  reload();
}

/** Rejoin the frame chain after a rollback. */
export function rejoinFrameChain(reload: () => void): void {
  setNewEngineEnabled(true);
  reload();
}
