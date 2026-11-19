/**
 * 摘要轮次锚点匹配：把 V2 摘要组件上抛的 turnId 关联回完整投影条目，
 * 从而复用既有的行级菜单 handler（复制 / 重放 / 分支 / 删除整轮）。
 *
 * 两侧对应关系（均已核对）：
 * - 摘要 TurnSummary.TurnId = "uturn-" + 用户消息 id（后端 Finalize）
 * - 投影 UserTurn.key = "uturn-" + userId，TimelineEntry(kind='user-turn').key = UserTurn.key
 * 因此正常情况下 turnId 与投影条目 key 同值，精确匹配即可命中；其余字段仅作兜底。
 */
import type { TimelineEntry } from './turnGroup';
import type { UserTurn } from './userTurn';

const USER_TURN_PREFIX = 'uturn-';

function userTurnOf(entry: TimelineEntry): UserTurn | null {
  if (entry.kind !== 'user-turn') return null;
  return (entry as { turn?: UserTurn }).turn ?? null;
}

/**
 * 按 turnId 从完整投影里找回用户轮次条目。
 * @param items 完整投影（不受历史区隐藏影响）
 * @param turnId 摘要组件携带的轮次 id
 */
export function findTurnEntry(items: TimelineEntry[], turnId: string): TimelineEntry | null {
  const id = String(turnId || '');
  if (!id) return null;
  const plainId = id.startsWith(USER_TURN_PREFIX) ? id.slice(USER_TURN_PREFIX.length) : id;

  // 主路径：键精确匹配（turnId、entry.key、turn.key 均为 uturn-<userId>）。
  for (const entry of items) {
    const turn = userTurnOf(entry);
    if (!turn) continue;
    if (entry.key === id || turn.key === id) return entry;
  }
  // 兜底：用户消息 id（turnId 去前缀后）匹配。
  if (plainId) {
    for (const entry of items) {
      const turn = userTurnOf(entry);
      if (turn && String(turn.userId) === plainId) return entry;
    }
  }
  // 兜底：请求 id 匹配（极端竞态 / pending 转正）。
  for (const entry of items) {
    const turn = userTurnOf(entry);
    if (turn && String(turn.requestId ?? '') === id) return entry;
  }
  return null;
}
