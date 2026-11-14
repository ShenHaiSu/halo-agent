import type { TimelineBlockContent } from '@shared/bridge/hostBridge';
import type { AgentToolCard } from '@features/agent/stores/model';
import { isToolErrorText } from './toolErrorText';

export interface ToolGroup {
  callId: string;
  callBlock?: TimelineBlockContent;
  resultBlock?: TimelineBlockContent;
  order: number;
  functionName: string;
}
function pickFunctionName(callBlock?: TimelineBlockContent, resultBlock?: TimelineBlockContent): string {
  const fromCall = String(callBlock?.functionName || '').trim();
  if (fromCall) return fromCall;
  const fromResult = String(resultBlock?.functionName || '').trim();
  if (fromResult) return fromResult;
  return 'tool';
}

function normalizeArgsJson(raw: unknown): string {
  const text = String(raw ?? '').trim();
  if (!text) return '{}';
  return String(raw ?? '{}');
}

/** 历史卡状态裁决：逐工具位 > 存量文本启发 > 轮级回退。 */
function resolveStatus(group: ToolGroup, turn: { failed: number; status: string }): 'done' | 'error' {
  const success = group.resultBlock?.success;
  if (success === true) return 'done';
  if (success === false) return 'error';
  if (isToolErrorText(group.resultBlock?.resultText)) return 'error';
  const failedCount = Number(turn?.failed || 0);
  const statusText = String(turn?.status || '').toLowerCase();
  return failedCount > 0 || statusText === 'error' ? 'error' : 'done';
}

export function pairToolGroupsInOrder(blocks: TimelineBlockContent[]): { tools: ToolGroup[]; others: TimelineBlockContent[] } {
  const list = Array.isArray(blocks) ? blocks : [];
  const groupsByCall = new Map<string, ToolGroup>();
  const others: TimelineBlockContent[] = [];
  const pendingResults: TimelineBlockContent[] = [];

  for (let index = 0; index < list.length; index++) {
    const block = list[index];
    if (!block) continue;
    const kind = String(block.kind || '');
    const callId = String(block.callId || '').trim();
    if (kind === 'tool_call' && callId) {
      let group = groupsByCall.get(callId);
      if (!group) {
        group = {
          callId,
          order: typeof block.seq === 'number' ? Number(block.seq) : index,
          functionName: 'tool',
        };
        groupsByCall.set(callId, group);
      }
      if (!group.callBlock) {
        group.callBlock = block;
      }
      continue;
    }
    if (kind === 'tool_result' && callId) {
      pendingResults.push(block);
      continue;
    }
    if ((kind === 'tool_call' || kind === 'tool_result') && !callId) {
      others.push(block);
      continue;
    }
    others.push(block);
  }

  for (const resultBlock of pendingResults) {
    const callId = String(resultBlock.callId || '').trim();
    const group = groupsByCall.get(callId);
    if (group && !group.resultBlock) {
      group.resultBlock = resultBlock;
    } else if (group && group.resultBlock) {
      continue;
    } else {
      others.push(resultBlock);
    }
  }

  const tools = Array.from(groupsByCall.values());
  for (const group of tools) {
    group.functionName = pickFunctionName(group.callBlock, group.resultBlock);
  }
  tools.sort((a, b) => a.order - b.order);
  return { tools, others };
}

export function toAgentToolCard(group: ToolGroup, turn: { failed: number; status: string }): AgentToolCard {
  const argsJson = normalizeArgsJson(group.callBlock?.argsJson);
  const status = resolveStatus(group, turn);
  const resultText = String(group.resultBlock?.resultText ?? '');
  return {
    callId: group.callId,
    blockId: group.callBlock?.blockId ?? null,
    seq: typeof group.callBlock?.seq === 'number' ? group.callBlock.seq : null,
    functionName: group.functionName || 'tool',
    argsJson,
    status,
    resultText,
    exitCode: null,
    durationMs: null,
    iteration: 0,
    receivedBytes: argsJson.length,
    fileBytes: null,
    images: [],
  };
}
