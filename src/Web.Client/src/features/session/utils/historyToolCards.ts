import type { TimelineBlockContent } from '@shared/bridge/hostBridge';
import type { AgentToolCard } from '@features/agent/stores/model';
import { isToolErrorText } from './toolErrorText';

export interface ToolGroup {
  callId: string;
  callBlock?: TimelineBlockContent;
  resultBlock?: TimelineBlockContent;
  functionName: string;
}

/**
 * 盒内渲染项：输出序与输入块序一致。
 * 调用方传入的块数组本身已是后端排好的时间序（请求返回序即渲染序），
 * 本函数只做“块到渲染项”的保序映射：不排序，不按类型分组，不重算顺序。
 * 工具卡占其调用块的位置；已被卡消费的结果块不再单独占位。
 */
export type HistoryItem =
  | { kind: 'tool'; key: string; group: ToolGroup }
  | { kind: 'reasoning'; key: string; block: TimelineBlockContent }
  | { kind: 'text'; key: string; block: TimelineBlockContent }
  | { kind: 'tool_result'; key: string; block: TimelineBlockContent };

export function buildHistoryItems(blocks: TimelineBlockContent[]): HistoryItem[] {
  const list = Array.isArray(blocks) ? blocks : [];
  const items: HistoryItem[] = [];
  // 仅用于配对查找，产出顺序始终以输入遍历为准，不以该表迭代拼装结果。
  const groups = new Map<string, ToolGroup>();

  for (const block of list) {
    if (!block) continue;
    const kind = String(block.kind || '');
    const callId = String(block.callId || '').trim();

    if (kind === 'tool_call' && callId) {
      const hit = groups.get(callId);
      if (hit) {
        if (!hit.callBlock) hit.callBlock = block;
        continue;
      }
      const group: ToolGroup = { callId, functionName: 'tool', callBlock: block };
      groups.set(callId, group);
      items.push({ kind: 'tool', key: `tool:${callId}`, group });
      continue;
    }

    if (kind === 'tool_result' && callId) {
      const group = groups.get(callId);
      if (group) {
        if (!group.resultBlock) group.resultBlock = block;
        continue;
      }
    }

    if (kind === 'reasoning') {
      items.push({ kind: 'reasoning', key: `rsn:${block.blockId}`, block });
      continue;
    }
    if (kind === 'tool_result') {
      items.push({ kind: 'tool_result', key: `res:${block.blockId}`, block });
      continue;
    }
    items.push({ kind: 'text', key: `txt:${block.blockId}`, block });
  }

  for (const group of groups.values()) {
    group.functionName = pickFunctionName(group.callBlock, group.resultBlock);
  }
  return items;
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
