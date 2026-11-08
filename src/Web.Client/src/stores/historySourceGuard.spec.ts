import { describe, expect, it } from 'vitest';
import { HISTORY_LEGACY_BADGE, historyWriteRoute, resolveSendBranch, titleRouteNote } from './historySourceGuard';

describe('history ownership guard', () => {
  it('keeps every mutation on the legacy chain', () => {
    expect(historyWriteRoute()).toBe('legacy');
    expect(HISTORY_LEGACY_BADGE).toBe('暂走legacy');
  });

  it('sends plain text through the frame branch only when bound and enabled', () => {
    expect(resolveSendBranch({ attachmentCount: 0, historyMode: 'frame', frameBound: true })).toBe('frame');
  });

  it('falls back to the legacy chain on any doubt', () => {
    expect(resolveSendBranch({ attachmentCount: 0, historyMode: 'legacy', frameBound: true })).toBe('legacy');
    expect(resolveSendBranch({ attachmentCount: 0, historyMode: 'frame', frameBound: false })).toBe('legacy');
    expect(resolveSendBranch({ attachmentCount: 2, historyMode: 'frame', frameBound: true })).toBe('legacy');
  });

  it('documents the title cutover without automatic triggers', () => {
    const note = titleRouteNote();
    expect(note).toContain('session.title');
    expect(note).toContain('TitleLocked');
  });
});
