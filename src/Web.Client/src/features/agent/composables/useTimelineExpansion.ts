import { computed, reactive } from 'vue';
import type { Ref } from 'vue';
import type { TimelineEntry } from '@features/session/utils/turnGroup';

/**
 * 展开态：用户轮显式展开 + 运行态合并舱展开。
 * 真相源为 timelineItems 的运行态推导，显式选择覆盖默认。
 */
export function useTimelineExpansion(timelineItems: Ref<TimelineEntry[]> | Readonly<Ref<TimelineEntry[]>>) {
  const expandedTurns = reactive<Record<string, boolean>>({});
  const isTurnExpanded = (key: string): boolean => expandedTurns[key] === true;
  function toggleTurn(key: string): void {
    expandedTurns[key] = !isTurnExpanded(key);
  }

  const runByKey = computed<Map<string, { key: string; isRunning: boolean }>>(() => {
    const m = new Map<string, { key: string; isRunning: boolean }>();
    for (const e of timelineItems.value) {
      if (e.kind === 'user-turn' && (e as unknown as { turn?: unknown }).turn) {
        const t = (e as unknown as { turn: { key: string; isRunning: boolean } }).turn;
        m.set(e.key, { key: String(t.key || e.key), isRunning: t.isRunning === true });
      }
    }
    return m;
  });

  const runExplicit = reactive<Record<string, boolean>>({});
  const isRunExpanded = (key: string): boolean => {
    if (key in runExplicit) return runExplicit[key] === true;
    const hit = runByKey.value.get(key);
    return !!hit && hit.isRunning;
  };
  function toggleRun(key: string): void {
    const next = !isRunExpanded(key);
    runExplicit[key] = next;
    expandedTurns[key] = next;
  }

  return { expandedTurns, isTurnExpanded, toggleTurn, runByKey, runExplicit, isRunExpanded, toggleRun };
}

export type TimelineExpansion = ReturnType<typeof useTimelineExpansion>;
