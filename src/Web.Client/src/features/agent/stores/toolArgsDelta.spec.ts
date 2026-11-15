// 回归：后端参数增量事件（kind=ToolCallUpdated + argsDelta）必须累加进 card.argsJson，
// 否则工具卡展开后参数区恒空。
import { beforeEach, describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { haloEventBus } from '@shared/bridge/hostBridge';
import { activeSessionId, getTurn } from './model';
import { useAgentCards } from './useAgentCards';

const SID = 'sess-test-1';

function turnEvent(kind: string, payload: Record<string, unknown>): void {
  haloEventBus.emitLocal('agent:turn-event', { sessionId: SID, kind, payload });
}

describe('工具卡参数增量累加（P1 回归）', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    activeSessionId.value = SID;
    useAgentCards().bindCards();
  });

  it('ToolCallStarted 后连续 argsDelta 累加成完整 argsJson', () => {
    turnEvent('ToolCallStarted', { iteration: 1, callId: 'c1', functionName: 'exec_command', receivedBytes: 0 });
    turnEvent('ToolCallUpdated', { iteration: 1, callId: 'c1', functionName: 'exec_command', argsDelta: '{"comm', receivedBytes: 6 });
    turnEvent('ToolCallUpdated', { iteration: 1, callId: 'c1', functionName: 'exec_command', argsDelta: 'and":"dir"}', receivedBytes: 17 });

    const card = getTurn(SID)?.toolCards.find((c) => c.callId === 'c1');
    expect(card).toBeDefined();
    expect(card!.argsJson).toBe('{"command":"dir"}');
  });

  it('全量 argsJson 覆盖增量拼接结果（不重复叠加）', () => {
    turnEvent('ToolCallStarted', { iteration: 1, callId: 'c2', functionName: 'read_file', receivedBytes: 0 });
    turnEvent('ToolCallUpdated', { iteration: 1, callId: 'c2', argsDelta: '{"file_pa', receivedBytes: 10 });
    turnEvent('ToolCallUpdated', { iteration: 1, callId: 'c2', argsJson: '{"file_path":"a.txt"}', receivedBytes: 21 });

    const card = getTurn(SID)?.toolCards.find((c) => c.callId === 'c2');
    expect(card!.argsJson).toBe('{"file_path":"a.txt"}');
  });
});
