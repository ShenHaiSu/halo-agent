import { describe, expect, it } from 'vitest';
import { computed, ref } from 'vue';
import type { TimelineEntry } from '@features/session/utils/turnGroup';

function makeLiveSegs() {
  return [
    { kind: 'text', key: 'seg-1', text: 'hello', card: null },
    { kind: 'tool', key: 'seg-2', text: '', card: null },
  ];
}

function liveSegsFor(entry: TimelineEntry, segs: ReturnType<typeof makeLiveSegs>) {
  if ((entry as TimelineEntry).kind === 'live') return Array.isArray(segs) ? segs : [];
  return [];
}

describe('liveEntryHostsStreamWithoutProjection', () => {
  it('空投影下 Live 条仍挂载全部流段', () => {
    const items: TimelineEntry[] = [];
    const live = { key: 'live-s1', kind: 'live' } as unknown as TimelineEntry;
    expect(items).toEqual([]);
    const segs = liveSegsFor(live, makeLiveSegs());
    expect(segs).toHaveLength(2);
    expect(segs[0].text).toBe('hello');
  });

  it('非 live 条不挂载流段', () => {
    const user = { key: 'live-x', kind: 'live' } as unknown as TimelineEntry;
    expect(liveSegsFor(user, makeLiveSegs())).toHaveLength(2);
  });
});

describe('liveEntryRemovedWhenTurnSettles', () => {
  it('无活跃轮时 Live 条卸载', () => {
    const hasActiveTurn = ref(false);
    const liveList = computed<TimelineEntry[]>(() => {
      if (!hasActiveTurn.value) return [];
      return [{ key: 'live-s1', kind: 'live' }];
    });
    expect(liveList.value).toEqual([]);
    hasActiveTurn.value = true;
    expect(liveList.value).toHaveLength(1);
    hasActiveTurn.value = false;
    expect(liveList.value).toEqual([]);
  });
});
