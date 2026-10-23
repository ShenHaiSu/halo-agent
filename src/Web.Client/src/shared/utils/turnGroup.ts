/** need21-F03：轮次聚合纯函数的 shared 入口（实现唯一源仍在 features/session；agent 域经此 import，不直连 feature-session，F05 再议物理搬迁）。 */
export {
  buildTurnGroups,
  buildTurnGroupsIncremental,
  buildTimelineEntries,
  imagesOf,
  mergeKeyOf,
  blockHash,
  exportSessionMarkdown,
} from '@features/session/utils/turnGroup';
export type { TurnGroup, TurnToolRef, TimelineEntry } from '@features/session/utils/turnGroup';
