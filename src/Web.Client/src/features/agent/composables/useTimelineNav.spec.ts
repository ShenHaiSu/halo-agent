import { describe, expect, it } from 'vitest';
import { createTimelineNav } from './useTimelineNav';

describe('useTimelineNav async ensureMounted + navEpoch', () => {
  it('mounts missing anchor via ensureMounted before jumping', async () => {
    const nav = createTimelineNav();
    const container = document.createElement('div');
    nav.setContainer(container);
    const el = document.createElement('div');
    let mounted = false;
    nav.setOrder(['a', 'b']);
    const ok = await nav.scrollToAnchor('b', {
      ensureMounted: async (key) => {
        if (key === 'b' && !mounted) {
          mounted = true;
          nav.register('b', el);
        }
        return true;
      },
    });
    expect(ok).toBe(true);
    expect(mounted).toBe(true);
  });

  it('drops stale jumps on rapid clicks (navEpoch)', async () => {
    const nav = createTimelineNav();
    nav.setContainer(document.createElement('div'));
    const elA = document.createElement('div');
    const elB = document.createElement('div');
    nav.register('a', elA);
    nav.register('b', elB);
    nav.setOrder(['a', 'b']);
    let releaseFirst!: () => void;
    const gate = new Promise<boolean>((resolve) => {
      releaseFirst = () => resolve(true);
    });
    const first = nav.scrollToAnchor('a', {
      ensureMounted: () => gate,
    });
    const second = nav.scrollToAnchor('b', {
      ensureMounted: async () => true,
    });
    releaseFirst();
    const [r1, r2] = await Promise.all([first, second]);
    expect(r1).toBe(false);
    expect(r2).toBe(true);
  });

  it('falls back to nearest and notifies toast callback', async () => {
    const nav = createTimelineNav();
    nav.setContainer(document.createElement('div'));
    const el = document.createElement('div');
    nav.register('a', el);
    nav.setOrder(['a', 'b', 'c']);
    let fallback = '';
    const ok = await nav.scrollToAnchor('c', {
      ensureMounted: async () => true,
      onFallback: (used) => {
        fallback = used;
      },
    });
    expect(ok).toBe(true);
    expect(fallback).toBe('a');
  });
});
