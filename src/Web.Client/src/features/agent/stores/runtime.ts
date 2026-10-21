/** need21-F03：agent 域运行时上下文 DI（旧 store 直读 workspace/session/config/askUser 的替代）。
 * 旧转发（stores/agentStore.ts）注册实现，新 store 只调 agentRuntime() 接口；单测可注入假实现。
 */
export interface AgentRuntimeContext {
  workspaceId(): string;
  providerId(): string;
  modelId(): string;
  maxImages(): number;
  findMessageContent(sessionId: string, msgId: string): string;
  truncateHistory(
    workspaceId: string,
    sessionId: string,
    msgId: string,
    removeAnchor: boolean
  ): Promise<{ ok: boolean; replayText: string }>;
  loadSessionMessages(sessionId: string): Promise<void>;
  refreshBudget(): Promise<void>;
  touchSessionTime(sessionId: string, iso?: string): void;
  generateTitle(sessionId: string): void;
  reconcileTimes(): void;
  askPending(sessionId: string): boolean;
  clearAskPending(sessionId: string): void;
}
const noopRuntime: AgentRuntimeContext = {
  workspaceId: () => '',
  providerId: () => '',
  modelId: () => '',
  maxImages: () => 4,
  findMessageContent: () => '',
  truncateHistory: () => Promise.resolve({ ok: false, replayText: '' }),
  loadSessionMessages: () => Promise.resolve(),
  refreshBudget: () => Promise.resolve(),
  touchSessionTime: () => undefined,
  generateTitle: () => undefined,
  reconcileTimes: () => undefined,
  askPending: () => false,
  clearAskPending: () => undefined,
};
let runtimeCtx: AgentRuntimeContext = noopRuntime;
/** 旧转发（stores/agentStore.ts）唯一调用处：注入 workspace/session/config/askUser 适配实现。 */
export function registerAgentRuntimeContext(ctx: Partial<AgentRuntimeContext>): void {
  runtimeCtx = { ...noopRuntime, ...ctx };
}
export function agentRuntime(): AgentRuntimeContext {
  return runtimeCtx;
}
