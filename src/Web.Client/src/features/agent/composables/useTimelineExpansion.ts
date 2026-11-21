import { computed, reactive } from 'vue';
import type { Ref } from 'vue';
import type { TimelineEntry } from '@features/session/utils/turnGroup';

/**
 * 展开态：用户轮显式展开 + 运行态合并舱展开。
 * 真相源为 timelineItems 的运行态推导，显式选择覆盖默认。
 */
export function useTimelineExpansion(_timelineItems?: Ref<TimelineEntry[]> | Readonly<Ref<TimelineEntry[]>>) {
  const expandedTurns = reactive<Record<string, boolean>>({});
  const isTurnExpanded = (key: string): boolean => expandedTurns[key] === true;
  function toggleTurn(key: string): void {
    expandedTurns[key] = !isTurnExpanded(key);
  }

  const runByKey = computed<Map<string, { key: string; isRunning: boolean }>>(() => {
    // C4：旧轮容器已删除，无运行态推导，返回空映射。
    return new Map<string, { key: string; isRunning: boolean }>();
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
