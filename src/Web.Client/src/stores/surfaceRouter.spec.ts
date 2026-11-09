import { describe, expect, it } from 'vitest';
import { SURFACES, SURFACE_TABLE, readOverride, resolveMode, type Surface } from './surfaceRouter';

function allCombinations<T>(items: readonly T[]): T[][] {
  return items.reduce<T[][]>((acc, item) => acc.concat(acc.map((combo) => [...combo, item])), [[]]);
}

describe('surface capability routing', () => {
  it('unimplemented surfaces always resolve to legacy across the full combination matrix', () => {
    const combos = allCombinations([...SURFACES]);
    expect(combos.length).toBe(32);
    for (const subset of combos) {
      for (const surface of SURFACES) {
        if (!SURFACE_TABLE[surface].implemented) {
          expect(resolveMode(surface, subset)).toBe('legacy');
        }
      }
    }
  });

  it('mixed states stay receivable: frame where implemented, legacy elsewhere', () => {
    expect(resolveMode('history', ['history'])).toBe(SURFACE_TABLE.history.implemented ? 'frame' : 'legacy');
    expect(resolveMode('turn', ['turn'])).toBe('frame');
    expect(resolveMode('tool', ['tool'])).toBe('frame');
    expect(resolveMode('usage', ['usage'])).toBe('frame');
    expect(resolveMode('settings', ['settings'])).toBe('frame');
  });

  it('usage and settings stay legacy by default and isolate from other faces', () => {
    expect(resolveMode('usage')).toBe('legacy');
    expect(resolveMode('settings')).toBe('legacy');
    expect(resolveMode('usage', ['history', 'turn', 'tool'])).toBe('legacy');
    expect(resolveMode('settings', ['history', 'turn', 'tool'])).toBe('legacy');
    expect(resolveMode('history', ['usage', 'settings'])).toBe('legacy');
  });

  it('override reader stays silent outside development builds', () => {
    const storage = { getItem: () => 'history,turn' };
    expect(readOverride(storage, '?surface=history', false)).toBeUndefined();
  });

  it('override reader merges storage and query lists in development builds', () => {
    const storage = { getItem: (key: string) => (key === 'halo-surface-modes' ? 'history' : null) };
    expect(readOverride(storage, '?surface=turn', true)).toEqual(['history', 'turn']);
  });

  it('override reader ignores unknown names and storage failures', () => {
    const storage = { getItem: () => 'unknown-surface' };
    expect(readOverride(storage, '', true)).toBeUndefined();
    const broken = {
      getItem: () => {
        throw new Error('private mode');
      },
    };
    expect(readOverride(broken, '?surface=history', true)).toBeUndefined();
    expect(readOverride(null, '', true)).toBeUndefined();
  });

  it('every surface row carries an owner for review routing', () => {
    const owners: Record<Surface, string> = {
      history: 'S3',
      turn: 'S6',
      tool: 'S6',
      usage: 'S7b',
      settings: 'S7b',
    };
    for (const surface of SURFACES) {
      expect(SURFACE_TABLE[surface].owner).toBe(owners[surface]);
      expect(SURFACE_TABLE[surface].mode).toBe('legacy');
    }
  });

  it('marks the usage and settings faces implemented while keeping the legacy default', () => {
    expect(SURFACE_TABLE.usage.implemented).toBe(true);
    expect(SURFACE_TABLE.settings.implemented).toBe(true);
    expect(SURFACE_TABLE.usage.mode).toBe('legacy');
    expect(SURFACE_TABLE.settings.mode).toBe('legacy');
  });
});
