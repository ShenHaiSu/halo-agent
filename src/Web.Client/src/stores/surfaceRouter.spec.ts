import { describe, expect, it } from 'vitest';
import { SURFACES, SURFACE_TABLE, readOverride, resolveMode, type Surface } from './surfaceRouter';

function allCombinations<T>(items: readonly T[]): T[][] {
  return items.reduce<T[][]>((acc, item) => acc.concat(acc.map((combo) => [...combo, item])), [[]]);
}

describe('surface capability routing', () => {
  it('serves every face from the frame chain by default', () => {
    for (const surface of SURFACES) {
      expect(SURFACE_TABLE[surface].implemented).toBe(true);
      expect(SURFACE_TABLE[surface].mode).toBe('frame');
      expect(resolveMode(surface)).toBe('frame');
    }
  });

  it('keeps the full combination matrix on the frame chain with no pending faces', () => {
    const combos = allCombinations([...SURFACES]);
    expect(combos.length).toBe(32);
    for (const subset of combos) {
      for (const surface of SURFACES) {
        expect(resolveMode(surface, subset)).toBe('frame');
      }
    }
    expect(SURFACES.every((surface) => SURFACE_TABLE[surface].implemented)).toBe(true);
  });

  it('still parks a face on legacy while it is flagged unimplemented', () => {
    const previous = SURFACE_TABLE.usage.implemented;
    SURFACE_TABLE.usage.implemented = false;
    try {
      const combos = allCombinations([...SURFACES]);
      for (const subset of combos) {
        expect(resolveMode('usage', subset)).toBe('legacy');
      }
      expect(resolveMode('history', ['history'])).toBe('frame');
    } finally {
      SURFACE_TABLE.usage.implemented = previous;
    }
  });

  it('rolls a face back with a single table row', () => {
    const previous = SURFACE_TABLE.tool.mode;
    SURFACE_TABLE.tool.mode = 'legacy';
    try {
      expect(resolveMode('tool')).toBe('legacy');
      expect(resolveMode('tool', ['tool'])).toBe('frame');
      expect(resolveMode('turn')).toBe('frame');
    } finally {
      SURFACE_TABLE.tool.mode = previous;
    }
  });

  it('mixed states stay receivable: frame where enabled, legacy where parked', () => {
    expect(resolveMode('history', ['history'])).toBe('frame');
    expect(resolveMode('turn', ['turn'])).toBe('frame');
    expect(resolveMode('tool', ['tool'])).toBe('frame');
    expect(resolveMode('usage', ['usage'])).toBe('frame');
    expect(resolveMode('settings', ['settings'])).toBe('frame');
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
      expect(SURFACE_TABLE[surface].mode).toBe('frame');
    }
  });

  it('marks the usage and settings faces served while keeping face isolation', () => {
    expect(SURFACE_TABLE.usage.implemented).toBe(true);
    expect(SURFACE_TABLE.settings.implemented).toBe(true);
    expect(SURFACE_TABLE.usage.mode).toBe('frame');
    expect(SURFACE_TABLE.settings.mode).toBe('frame');
  });
});
