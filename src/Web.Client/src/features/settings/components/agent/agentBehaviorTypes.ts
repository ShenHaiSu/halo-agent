/** 智能体行为页的卡片名与字段归属，供状态、校验、视图共用（避免字符串漂移）。 */

export type AgentBehaviorCardName = 'timeout' | 'iterations' | 'subagents' | 'monitor' | 'rejection';

export const AGENT_BEHAVIOR_CARD_NAMES: readonly AgentBehaviorCardName[] = ['timeout', 'iterations', 'subagents', 'monitor', 'rejection'];

/** 可编辑配置字段 → 所属卡片。监控/拒绝卡不承载配置字段，永不显示配置脏点。 */
export const CONFIG_FIELD_CARD: Readonly<Record<string, AgentBehaviorCardName>> = {
  execDefaultTimeoutMs: 'timeout',
  execMaxTimeoutMs: 'timeout',
  maxIterations: 'iterations',
  turnTimeoutMs: 'iterations',
  subAgentEnabled: 'subagents',
  subMaxParallel: 'subagents',
  subMaxIterations: 'subagents',
  subAllowExecCommand: 'subagents',
  subAllowFileWrite: 'subagents',
  subAllowSkillWrite: 'subagents',
  subAllowMemoryWrite: 'subagents',
  subAllowAskUser: 'subagents',
  subPerms: 'subagents',
};

/** 后端校验错误字段 → 卡片；未知字段回落到最可能的配置卡。 */
export function fieldToCard(field: string | null | undefined): AgentBehaviorCardName {
  if (!field) return 'timeout';
  return CONFIG_FIELD_CARD[field] ?? 'timeout';
}
