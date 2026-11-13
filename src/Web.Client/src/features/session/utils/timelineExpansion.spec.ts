import { describe, expect, it } from 'vitest';
import { quotaLimit, setTurnExpanded, expandedBoxCount, clearExpansion } from './timelineExpansion';

describe('timelineExpansion quota', () => {
  it('starts at 600 with no expanded boxes', () => {
    clearExpansion();
    expect(expandedBoxCount()).toBe(0);
    expect(quotaLimit()).toBe(600);
  });

  it('grows 64 per expanded box and caps at 2000', () => {
    clearExpansion();
    setTurnExpanded('uturn-a', true);
    setTurnExpanded('uturn-b', true);
    expect(expandedBoxCount()).toBe(2);
    expect(quotaLimit()).toBe(600 + 128);
    for (let i = 0; i < 30; i++) setTurnExpanded(`uturn-${i}`, true);
    expect(quotaLimit()).toBe(2000);
    setTurnExpanded('uturn-a', false);
    expect(quotaLimit()).toBeLessThanOrEqual(2000);
    clearExpansion();
  });
});
