import { describe, expect, it } from 'vitest';
import type { ContractEventFrame } from '../../../contract/agent';
import { TurnProjectionBoard } from './turnProjection';

let seq = 0;
function frame(kind: ContractEventFrame['k'], params: Record<string, unknown>): ContractEventFrame {
  seq += 1;
  return { t: 'evt', seq, k: kind, p: params };
}

function fullTurn(board: TurnProjectionBoard, turnId: string, sessionId: string): void {
  board.observe(frame('turn.started', { turnId, sessionId }));
  board.observe(frame('text.delta', { turnId, sessionId, delta: 'hel' }));
  board.observe(frame('text.delta', { turnId, sessionId, delta: 'lo' }));
  board.observe(frame('tool.start', { turnId, sessionId, toolCallId: 'call-1', toolName: 'reply' }));
  board.observe(frame('tool.delta', { turnId, sessionId, toolCallId: 'call-1', delta: '{"a":' }));
  board.observe(frame('tool.delta', { turnId, sessionId, toolCallId: 'call-1', delta: '1}' }));
  board.observe(frame('tool.end', { turnId, sessionId, toolCallId: 'call-1', ok: true }));
  board.observe(frame('turn.done', { turnId, sessionId }));
}

describe('turn projection board', () => {
  it('folds one turn into text plus a finished tool card', () => {
    seq = 0;
    const board = new TurnProjectionBoard();
    fullTurn(board, 'turn-1', 'session-a');
    const card = board.turnOf('turn-1');
    expect(card?.text).toBe('hello');
    expect(card?.status).toBe('done');
    expect(card?.tools).toHaveLength(1);
    expect(card?.tools[0]?.argsText).toBe('{"a":1}');
    expect(card?.tools[0]?.ok).toBe(true);
    expect(board.turnsOfSession('session-a')).toHaveLength(1);
  });

  it('marks turns that wait for a human answer', () => {
    seq = 0;
    const board = new TurnProjectionBoard();
    board.observe(frame('turn.started', { turnId: 'turn-2', sessionId: 'session-a' }));
    board.observe(frame('tool.confirm', { turnId: 'turn-2', sessionId: 'session-a', toolCallId: 'call-9' }));
    expect(board.turnOf('turn-2')?.status).toBe('awaiting');
    board.observe(frame('turn.cancelled', { turnId: 'turn-2', sessionId: 'session-a' }));
    expect(board.turnOf('turn-2')?.status).toBe('cancelled');
  });

  it('ignores unknown frames and empty ids', () => {
    seq = 0;
    const board = new TurnProjectionBoard();
    fullTurn(board, 'turn-3', 'session-b');
    board.observe(frame('usage.tick', { turnId: 'turn-3', inputTokens: 3 }));
    board.observe(frame('turn.started', { sessionId: 'session-b' }));
    board.observe(frame('text.delta', { delta: 'stray' }));
    expect(board.turnsOfSession('session-b')).toHaveLength(1);
    expect(board.turnsOfSession('missing')).toEqual([]);
  });

  it('rebuilds from a fresh poll after reset', () => {
    seq = 0;
    const board = new TurnProjectionBoard();
    fullTurn(board, 'turn-4', 'session-c');
    board.reset();
    expect(board.turnsOfSession('session-c')).toEqual([]);
    fullTurn(board, 'turn-4', 'session-c');
    expect(board.turnOf('turn-4')?.status).toBe('done');
  });
});
