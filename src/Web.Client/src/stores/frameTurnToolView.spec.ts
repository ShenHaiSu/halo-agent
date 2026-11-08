import { describe, expect, it } from 'vitest';
import { resolveMode, type Surface } from './surfaceRouter';
import { describeMixedState, resolveToolMode, resolveTurnMode, selectToolSource, selectTurnSource } from './frameTurnToolView';

describe('frame turn and tool faces', () => {
  it('resolves both faces to frame through the development override', () => {
    expect(resolveTurnMode(['turn', 'tool'])).toBe('frame');
    expect(resolveToolMode(['turn', 'tool'])).toBe('frame');
    expect(resolveTurnMode([])).toBe('legacy');
    expect(resolveToolMode([])).toBe('legacy');
  });

  it('keeps history on legacy while turn and tool ride frame', () => {
    const override: readonly Surface[] = ['turn', 'tool'];
    expect(resolveMode('history', override)).toBe('legacy');
    expect(resolveTurnMode(override)).toBe('frame');
    expect(resolveToolMode(override)).toBe('frame');
    const mixed = describeMixedState({ history: 'legacy', turn: 'frame', tool: 'frame' });
    expect(mixed.mixed).toBe(true);
    expect(mixed.safe).toBe(true);
    expect(mixed.rollback).toContain('legacy');
  });

  it('picks frame data only on frame faces and rolls back by flipping modes', () => {
    expect(selectTurnSource('frame', 'legacy-text', 'frame-text')).toBe('frame-text');
    expect(selectTurnSource('legacy', 'legacy-text', 'frame-text')).toBe('legacy-text');
    expect(selectToolSource('frame', 1, 2)).toBe(2);
    expect(selectToolSource('legacy', 1, 2)).toBe(1);
    const rolled = describeMixedState({ history: 'legacy', turn: 'legacy', tool: 'legacy' });
    expect(rolled.mixed).toBe(false);
    expect(rolled.safe).toBe(true);
  });
});
