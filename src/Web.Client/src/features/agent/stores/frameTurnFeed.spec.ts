import { describe, expect, it } from 'vitest';
import type { ContractEventFrame } from '@/contract/agent';
import { SMOOTH_CATCHUP_S, SMOOTH_CPS, SMOOTH_MAX_BACKLOG } from '@features/agent/composables/useSmoothText';
import { FrameTurnFeed, TURN_STREAM_TUNING, describeCancel, describePromptFailure, describeTurnFailure } from './frameTurnFeed';

let seq = 0;
function frame(kind: ContractEventFrame['k'], params: Record<string, unknown>): ContractEventFrame {
  seq += 1;
  return { t: 'evt', seq, k: kind, p: params };
}

describe('frame turn feed stream assembly', () => {
  it('assembles multi-slice text of one turn without visual drift from legacy tuning', () => {
    seq = 0;
    const feed = new FrameTurnFeed();
    expect(feed.observe(frame('turn.started', { turnId: 'turn-1', sessionId: 'sess-1' }))).toBe(true);
    expect(feed.observe(frame('text.delta', { turnId: 'turn-1', sessionId: 'sess-1', delta: 'hel' }))).toBe(true);
    expect(feed.observe(frame('text.delta', { turnId: 'turn-1', sessionId: 'sess-1', delta: 'lo' }))).toBe(true);
    expect(feed.turnOf('turn-1')?.text).toBe('hello');
    expect(TURN_STREAM_TUNING.charsPerSecond).toBe(SMOOTH_CPS);
    expect(TURN_STREAM_TUNING.catchupSeconds).toBe(SMOOTH_CATCHUP_S);
    expect(TURN_STREAM_TUNING.maxBacklog).toBe(SMOOTH_MAX_BACKLOG);
  });

  it('dedups cursor replays of the same turn and keeps waters observable', () => {
    seq = 0;
    const feed = new FrameTurnFeed();
    feed.observe(frame('turn.started', { turnId: 'turn-2', sessionId: 'sess-1' }));
    feed.observe(frame('text.delta', { turnId: 'turn-2', sessionId: 'sess-1', delta: 'a' }));
    const duplicate: ContractEventFrame = { t: 'evt', seq: 2, k: 'text.delta', p: { turnId: 'turn-2', sessionId: 'sess-1', delta: 'a' } };
    expect(feed.observe(duplicate)).toBe(false);
    expect(feed.turnOf('turn-2')?.text).toBe('a');
    const snapshot = feed.stats();
    expect(snapshot.dedupDrops).toBe(1);
    expect(snapshot.waters).toBe(2);
    expect(snapshot.lastSeq).toBe(2);
    expect(feed.lastSeq).toBe(2);
  });

  it('rejects frames carrying stale incremental-args keys and counts them', () => {
    seq = 0;
    const feed = new FrameTurnFeed();
    feed.observe(frame('turn.started', { turnId: 'turn-3', sessionId: 'sess-1' }));
    const bad: ContractEventFrame = {
      t: 'evt',
      seq: 99,
      k: 'text.delta',
      p: { turnId: 'turn-3', sessionId: 'sess-1', delta: 'x', argsDelta: 'x' },
    };
    expect(feed.observe(bad)).toBe(false);
    expect(feed.turnOf('turn-3')?.text).toBe('');
    expect(feed.stats().rejectedLegacyArgs).toBe(1);
    expect(feed.stats().waters).toBe(99);
  });

  it('records failure reason and hint for the outcome card', () => {
    seq = 0;
    const feed = new FrameTurnFeed();
    feed.observe(frame('turn.started', { turnId: 'turn-4', sessionId: 'sess-1' }));
    feed.observe(frame('turn.failed', { turnId: 'turn-4', sessionId: 'sess-1', reason: 'NETWORK_FAIL', hint: 'timeout after 30s' }));
    const turn = feed.turnOf('turn-4');
    expect(turn?.status).toBe('failed');
    expect(turn?.reason).toBe('NETWORK_FAIL');
    expect(turn?.hint).toBe('timeout after 30s');
    expect(describeTurnFailure(turn?.reason ?? '', turn?.hint ?? '')).toContain('NETWORK_FAIL');
  });

  it('distinguishes a failed cancel from a real one via the signal flag', () => {
    seq = 0;
    const feed = new FrameTurnFeed();
    feed.observe(frame('turn.started', { turnId: 'turn-5', sessionId: 'sess-1' }));
    feed.observe(frame('turn.cancelled', { turnId: 'turn-5', sessionId: 'sess-1', signaled: false }));
    expect(feed.turnOf('turn-5')?.status).toBe('cancelled');
    expect(feed.turnOf('turn-5')?.signaled).toBe(false);
    expect(describeCancel(false, '')).toContain('取消失败');
    expect(describeCancel(true, '')).toBe('已停止生成。');
    expect(describeCancel(true, 'rejected')).toContain('拒绝');
    expect(describeCancel(true, 'timeout')).toContain('超时');
  });

  it('passes reasoning slices through to the reasoning channel only', () => {
    seq = 0;
    const feed = new FrameTurnFeed();
    feed.observe(frame('turn.started', { turnId: 'turn-6', sessionId: 'sess-1' }));
    feed.observe(frame('text.delta', { turnId: 'turn-6', sessionId: 'sess-1', delta: 'think', kind: 'reasoning' }));
    feed.observe(frame('text.delta', { turnId: 'turn-6', sessionId: 'sess-1', delta: 'say' }));
    expect(feed.turnOf('turn-6')?.reasoning).toBe('think');
    expect(feed.turnOf('turn-6')?.text).toBe('say');
  });

  it('records the history-index section carried by the start event', () => {
    seq = 0;
    const feed = new FrameTurnFeed();
    feed.observe(frame('turn.started', { turnId: 'turn-7', sessionId: 'sess-1', historyIndex: 4 }));
    expect(feed.turnOf('turn-7')?.sectionIndex).toBe(4);
  });

  it('rebuilds from a snapshot cursor after a gap', () => {
    seq = 0;
    const feed = new FrameTurnFeed();
    feed.observe(frame('turn.started', { turnId: 'turn-8', sessionId: 'sess-1' }));
    feed.noteResync(120);
    expect(feed.turnsOfSession('sess-1')).toEqual([]);
    expect(feed.stats().resyncs).toBe(1);
    expect(feed.stats().waters).toBe(120);
  });
});

describe('frame prompt failure copy', () => {
  it('keeps reentry, compacting, empty, auth, rate and network wording stable', () => {
    expect(describePromptFailure('TURN_IN_PROGRESS', { activeRequest: 'req-9' }).message).toContain('req-9');
    expect(describePromptFailure('TURN_IN_PROGRESS', {}).message).toContain('unknown');
    expect(describePromptFailure('COMPACTING').message).toContain('压缩中');
    expect(describePromptFailure('INVALID_PARAMS').retry).toBe(false);
    expect(describePromptFailure('PROVIDER_AUTH').message).toContain('授权');
    expect(describePromptFailure('NETWORK_FAIL').retry).toBe(true);
    expect(describePromptFailure('RATE_LIMITED', { hint: 'backpressure' }).message).toContain('backpressure');
    expect(describePromptFailure('RATE_LIMITED', {}).retry).toBe(true);
  });
});
