import { describe, expect, it, beforeEach } from 'vitest';
import { SURFACE_TABLE, SURFACES } from '../stores/surfaceRouter';
import { NEW_ENGINE_KEY, isNewEngineEnabled } from './legacy';
import {
  describeChainProbe,
  emptyChainProbe,
  missingBatches,
  parseBatchList,
  rejoinFrameChain,
  rollbackToLegacyChain,
} from './engineChain';

describe('engine chain cutover', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('keeps the triple default on the frame chain', () => {
    expect(isNewEngineEnabled()).toBe(true);
    for (const surface of SURFACES) {
      expect(SURFACE_TABLE[surface].mode).toBe('frame');
      expect(SURFACE_TABLE[surface].implemented).toBe(true);
    }
  });

  it('rolls back with one explicit flip and rejoins the same way', () => {
    let reloads = 0;
    rollbackToLegacyChain(() => (reloads += 1));
    expect(localStorage.getItem(NEW_ENGINE_KEY)).toBe('false');
    expect(isNewEngineEnabled()).toBe(false);
    expect(reloads).toBe(1);
    rejoinFrameChain(() => (reloads += 1));
    expect(isNewEngineEnabled()).toBe(true);
    expect(reloads).toBe(2);
  });

  it('describes the capability probe for the self-check', () => {
    const probe = describeChainProbe({
      routing: { enabledBatches: ['A', 'B', 'C'], defaultFrame: true },
      batches: ['A', 'B', 'C'],
      bridgeVersion: 2,
      contractVersion: '0.1.1',
    });
    expect(probe.batches).toEqual(['A', 'B', 'C']);
    expect(probe.frameDefault).toBe(true);
    expect(probe.bridgeVersion).toBe(2);
    expect(probe.contractVersion).toBe('0.1.1');
    expect(missingBatches(probe)).toEqual([]);
  });

  it('badges missing probe fields instead of guessing', () => {
    const probe = describeChainProbe({ routing: { enabledBatches: ['A'] } });
    expect(probe.batches).toEqual(['A']);
    expect(probe.frameDefault).toBeNull();
    expect(probe.bridgeVersion).toBeNull();
    expect(probe.contractVersion).toBeNull();
    expect(missingBatches(probe)).toEqual(['B', 'C']);
    expect(describeChainProbe({})).toEqual(emptyChainProbe());
  });

  it('drops unknown batch entries from the probe', () => {
    expect(parseBatchList(['A', 'Z', 7, 'b'])).toEqual(['A']);
    expect(parseBatchList(null)).toEqual([]);
  });
});
