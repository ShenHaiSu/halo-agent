/** need21-F02: 桥协议类型（单源：旧 src/bridge/types.ts 按域拆分，行为不变）。 */
import type { ModelConfig, ProviderConfig } from './providers';
// =========================================================================
// Stage 03: LLM 网关统一请求模型与流式事件 DTO
// =========================================================================

export type UnifiedStreamEventKind =
  | 'StreamStarted'
  | 'ReasoningDelta'
  | 'TextDelta'
  | 'ToolCallStarted'
  | 'ToolCallArgsDelta'
  | 'ToolCallCompleted'
  | 'UsageUpdated'
  | 'StreamCompleted'
  | 'StreamCancelled'
  | 'StreamError';

export interface UnifiedToolCallDto {
  callId: string;
  itemId?: string | null;
  functionName: string;
  argumentsJson: string;
}

export interface UnifiedTokenUsageDto {
  inputTokens: number;
  outputTokens: number;
  cachedInputTokens: number;
  totalTokens: number;
  cacheHitRate?: number;
}

export interface SessionStreamEventDto {
  sessionId: string;
  kind: UnifiedStreamEventKind | string;
  textDelta?: string | null;
  toolCall?: UnifiedToolCallDto | null;
  usage?: UnifiedTokenUsageDto | null;
  errorCode?: string | null;
  errorMessage?: string | null;
}

export interface UnifiedChatMessageDto {
  role: 'system' | 'user' | 'assistant' | 'tool';
  content?: string | null;
  name?: string | null;
  toolCallId?: string | null;
  toolCalls?: Array<{
    id: string;
    type: string;
    function: {
      name: string;
      arguments: string;
    };
  }>;
}

export interface UnifiedToolDefinitionDto {
  name: string;
  description: string;
  parametersJson: string;
}

export interface HeaderResolutionContextDto {
  sessionId: string;
  sessionHash: string;
  workspaceName: string;
  workspacePath: string;
  workspaceHash: string;
  modelId: string;
  providerName: string;
  providerId: string;
  userCustomEnv: string;
}

export interface UnifiedLlmRequestDto {
  requestId: string;
  provider: ProviderConfig;
  model: ModelConfig;
  messages: UnifiedChatMessageDto[];
  systemPrompt?: string;
  temperature?: number;
  maxTokens?: number;
  tools?: UnifiedToolDefinitionDto[];
  headerContext?: HeaderResolutionContextDto;
}
