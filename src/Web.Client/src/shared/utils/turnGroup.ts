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

/** need-01：无正文轮次合并——同一 shared 入口约定（agent 域不直连 feature-session）。 */
export {
  buildTurnRuns,
  makeTurnRun,
  isGhostTurn,
  hasRunDetail,
  failedCountOfGroup,
  runSummaryText,
  runPhaseOf,
  runPhaseLabel,
  runDurationLabel,
} from '@features/session/utils/turnRun';
export type { TurnRun, BuildTurnRunsOpts } from '@features/session/utils/turnRun';


/** need02-P2：用户轮次聚合——同一 shared 入口约定（实现唯一源在 features/session/utils/userTurn）。 */
export {
  buildUserTurns,
  isSingleFlatTurn,
  lastStepContent,
  earlierSteps,
  summaryOf,
  fullSummaryOf,
  phaseIcon,
  rememberPendingAlias,
  pendingAliasOf,
  clearPendingAlias,
} from '@features/session/utils/userTurn';
export type { UserTurn, UserTurnStep, UserTurnEntry, BuildUserTurnsOpts } from '@features/session/utils/userTurn';
