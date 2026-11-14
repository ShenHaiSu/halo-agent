import { useTimelineViewModel } from './useTimelineViewModel';

export type { DisplaySeg } from './useTimelineProjection';

/**
 * 旧出口薄包装：内部转调视图模型，保持调用方零改动。
 * @deprecated 新代码请直接使用 useTimelineViewModel。
 */
export function useTimeline() {
  return useTimelineViewModel();
}

export type Timeline = ReturnType<typeof useTimeline>;
