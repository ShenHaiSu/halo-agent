// Ownership for history reads and writes while the frame chain rolls out.
// Reads may come from the frame boundary in development; every mutation
// (surgery, export, pinning, workspace moves, sub-sessions, titles) keeps
// riding the legacy bridge, badged in the interface, until an explicit cutover.

import type { SurfaceMode } from './surfaceRouter';

/** Corner badge marking entries that still read through the legacy chain. */
export const HISTORY_LEGACY_BADGE = '暂走legacy';

export type SendBranch = 'frame' | 'legacy';

export interface SendBranchInput {
  attachmentCount: number;
  historyMode: SurfaceMode;
  frameBound: boolean;
}

/**
 * Send-path selector for the composer: plain text may ride the frame branch
 * while the history face resolves to frame and a frame transport is bound.
 * Anything with attachments, without a bound transport, or on the legacy
 * face stays on the legacy chain. Any doubt resolves to legacy.
 */
export function resolveSendBranch(input: SendBranchInput): SendBranch {
  try {
    if (input.attachmentCount > 0) return 'legacy';
    if (!input.frameBound) return 'legacy';
    return input.historyMode === 'frame' ? 'frame' : 'legacy';
  } catch {
    return 'legacy';
  }
}

/** Write operations stay on the legacy chain at this stage. */
export function historyWriteRoute(): 'legacy' {
  return 'legacy';
}

/** Title entries keep legacy writes; see the note for the later cutover. */
export function titleRouteNote(): string {
  return '标题入口仍走 legacy 写入；后续切换使用显式的 session.title 调用并携带 TitleLocked，不做自动触发。';
}
