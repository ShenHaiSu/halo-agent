/**
 * need22-F04：用户消息分段（文本段 + skill 段），供历史气泡渲染 chip。
 * 放在 features/agent（features/agent 不得 import features/session，eslint boundaries）。
 */
import { splitBySkillMarkers } from '@shared/utils/skillMarker';

export type UserContentPart = { type: 'text'; text: string } | { type: 'skill'; scope: 'global' | 'project'; name: string };

export function userParts(content: string): UserContentPart[] {
  return splitBySkillMarkers(String(content ?? '')) as UserContentPart[];
}
