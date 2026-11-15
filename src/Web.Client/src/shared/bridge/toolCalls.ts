import type { UnifiedChatMessage } from './protocol/domain';

export interface ToolCallRef {
  id: string;
  name: string;
  argsJson: string;
}

/**
 * 桥接工具调用形状守卫：兼容新旧两种载荷。
 * 新形状走 message.toolCalls（结构化），旧形状走透传字段。
 * 失败时返回空数组，调用方按无工具处理。
 */
export function toolCallsOf(msg: UnifiedChatMessage | null | undefined): ToolCallRef[] {
  try {
    if (!msg) return [];
    const direct = (msg as UnifiedChatMessage).toolCalls;
    if (Array.isArray(direct)) {
      return direct.map((c) => ({
        id: String(c.id || ''),
        name: String(c.function?.name || ''),
        argsJson: String(c.function?.arguments || ''),
      }));
    }
    const legacy = msg as unknown as {
      toolCalls?: Array<{
        id?: string;
        callId?: string;
        argumentsJson?: string;
        function?: { arguments?: string; name?: string };
        functionName?: string;
        name?: string;
      }>;
    };
    if (Array.isArray(legacy.toolCalls)) {
      return legacy.toolCalls.map((c) => ({
        id: String(c.id || c.callId || ''),
        name: String(c.function?.name || c.functionName || c.name || ''),
        argsJson: String(c.argumentsJson || c.function?.arguments || ''),
      }));
    }
  } catch (e) {
    console.warn('[bridge] tool calls shape read failed:', e);
  }
  return [];
}
