import type { AgentConfigDocument } from '@shared/bridge/hostBridge';
import { fieldToCard, type AgentBehaviorCardName } from '../agentBehaviorTypes';

export interface AgentBehaviorValidationIssue {
  key: keyof AgentConfigDocument | 'subPerms';
  card: AgentBehaviorCardName;
  message: string;
}

const EXEC_TIMEOUT_MAX = 3_600_000;
const MAX_ITERATIONS_MAX = 500;
const TURN_TIMEOUT_MIN = 1_000;
const TURN_TIMEOUT_MAX = 600_000;
const SUB_MAX_PARALLEL_MAX = 8;
const SUB_MAX_ITERATIONS_MAX = 100;

function isWithin(value: number, min: number, max: number): boolean {
  return Number.isFinite(value) && value >= min && value <= max;
}

function isUnlimitedOrWithin(value: number, min: number, max: number): boolean {
  return value === 0 || isWithin(value, min, max);
}

/** 保存前的纯校验：按固定顺序返回字段错误，不访问界面或宿主桥。 */
export function validateAgentBehavior(config: AgentConfigDocument): AgentBehaviorValidationIssue[] {
  const issues: AgentBehaviorValidationIssue[] = [];
  const addIssue = (key: keyof AgentConfigDocument | 'subPerms', message: string): void => {
    issues.push({ key, card: fieldToCard(key), message });
  };

  const defaultTimeoutValid = isUnlimitedOrWithin(config.execDefaultTimeoutMs, 1, EXEC_TIMEOUT_MAX);
  const maxTimeoutValid = isUnlimitedOrWithin(config.execMaxTimeoutMs, 1, EXEC_TIMEOUT_MAX);
  const turnTimeoutMs = config.turnTimeoutMs ?? 0;

  if (!defaultTimeoutValid) {
    addIssue('execDefaultTimeoutMs', `默认超时必须为 0（不限）或 1..${EXEC_TIMEOUT_MAX} 毫秒`);
  }
  if (!maxTimeoutValid) {
    addIssue('execMaxTimeoutMs', `上限超时必须为 0（不限）或 1..${EXEC_TIMEOUT_MAX} 毫秒`);
  }
  if (!isWithin(config.maxIterations, 0, MAX_ITERATIONS_MAX)) {
    addIssue('maxIterations', `最大轮次必须为 0..${MAX_ITERATIONS_MAX}`);
  }
  if (!isUnlimitedOrWithin(turnTimeoutMs, TURN_TIMEOUT_MIN, TURN_TIMEOUT_MAX)) {
    addIssue('turnTimeoutMs', `单轮超时必须为 0（不限）或 ${TURN_TIMEOUT_MIN}..${TURN_TIMEOUT_MAX} 毫秒`);
  }
  if (!isWithin(config.subMaxParallel, 1, SUB_MAX_PARALLEL_MAX)) {
    addIssue('subMaxParallel', `子代理并发数必须为 1..${SUB_MAX_PARALLEL_MAX}`);
  }
  if (!isWithin(config.subMaxIterations, 1, SUB_MAX_ITERATIONS_MAX)) {
    addIssue('subMaxIterations', `子代理最大轮次必须为 1..${SUB_MAX_ITERATIONS_MAX}`);
  }
  // 范围错误已定位字段时不再追加重复的跨字段提示。
  if (defaultTimeoutValid && maxTimeoutValid && config.execMaxTimeoutMs > 0 && config.execDefaultTimeoutMs > config.execMaxTimeoutMs) {
    addIssue('execDefaultTimeoutMs', '默认超时不得大于执行超时上限');
  }

  return issues;
}
