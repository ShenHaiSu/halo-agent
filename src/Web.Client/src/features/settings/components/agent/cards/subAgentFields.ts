/**
 * need23：子代理配置卡的可编辑字段契约（卡片与父视图共享，避免字符串字段名漂移）。
 * - 布尔/数值字段名与后端 AgentConfigDocument 同名（直接写 agent.json）。
 * - "恢复默认"用后端 reset 别名（AgentConfigService.ResetAgentConfig 两套都认）。
 */
import type { AgentConfigDocument } from '@shared/bridge/hostBridge';

/** 可写布尔字段（总开关 + 4 个权限）。 */
export const SUB_BOOL_FIELDS = [
  'subAgentEnabled',
  'subAllowExecCommand',
  'subAllowFileWrite',
  'subAllowSkillWrite',
  'subAllowMemoryWrite',
] as const;

/** 可写数值字段（并发上限 / 子轮次上限）。 */
export const SUB_NUM_FIELDS = ['subMaxParallel', 'subMaxIterations'] as const;

export type SubBoolField = (typeof SUB_BOOL_FIELDS)[number];
export type SubNumField = (typeof SUB_NUM_FIELDS)[number];

/** 后端 reset 字段别名。 */
export const SUB_RESET_FIELDS = ['subEnabled', 'subParallel', 'subIterations', 'subPerms'] as const;
export type SubResetField = (typeof SUB_RESET_FIELDS)[number];

/** 数值边界（对齐后端 AgentDefaults：并发 1..8，轮次 1..100）。 */
export const SUB_MAX_PARALLEL_MIN = 1;
export const SUB_MAX_PARALLEL_MAX = 8;
export const SUB_MAX_ITERATIONS_MIN = 1;
export const SUB_MAX_ITERATIONS_MAX = 100;

/** 卡片所需配置视图。 */
export type SubAgentConfig = Pick<AgentConfigDocument, SubBoolField | SubNumField | 'subAllowAskUser'>;

/** 数值裁剪（清空输入等场景下避免写入 0 触发后端 AGENT_VALIDATION）。 */
export function clampSubNum(field: SubNumField, v: number | null | undefined): number {
  const raw = Number.isFinite(v) ? Math.trunc(Number(v)) : 0;
  if (field === 'subMaxParallel') {
    return Math.min(SUB_MAX_PARALLEL_MAX, Math.max(SUB_MAX_PARALLEL_MIN, raw || SUB_MAX_PARALLEL_MIN));
  }
  return Math.min(SUB_MAX_ITERATIONS_MAX, Math.max(SUB_MAX_ITERATIONS_MIN, raw || SUB_MAX_ITERATIONS_MIN));
}
