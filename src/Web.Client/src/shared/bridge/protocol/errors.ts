/** need21-F02: 桥协议错误码 + 上报 + Date 序列化（旧 src/bridge/types.ts 原样迁移，行为不变）。 */

/** Mock 显式横幅文案（红色横幅 + 重试按钮，禁止静默 Mock）。 */
export const MOCK_BANNER_TEXT = '桥接未连接（Mock数据）';

/** 历史操作稳定错误码：前端按码提示与判断重试，不依赖异常字符串。 */
export type HistoryErrorCode =
  | 'ANCHOR_NOT_FOUND'
  | 'INVALID_ANCHOR_ROLE'
  | 'INVALID_BRANCH_REQUEST'
  | 'SESSION_BUSY'
  | 'HISTORY_OPERATION_IN_PROGRESS'
  | 'SUB_READONLY'
  | 'HISTORY_READONLY'
  | 'EMPTY_REPLAY_TEXT'
  | 'NO_PRIOR_USER'
  | 'REWRITE_ERROR'
  | 'TRUNCATE_COMMIT_ERROR'
  | 'BRANCH_COPY_ERROR'
  | 'BRANCH_ROLLBACK_FAILED'
  | 'REPLAY_SEND_FAILED'
  | 'REPLAY_TARGET_CHANGED'
  | 'IMAGE_MISSING'
  | 'TRUNCATE_ERROR'
  | 'BRANCH_ERROR'
  | 'MESSAGE_NOT_FOUND'
  | 'BACKUP_FAILED'
  | 'DELETE_COMMIT_FAILED'
  | 'DELETE_COMMIT_UNKNOWN'
  | 'TURN_DELETE_UNSUPPORTED';

const HISTORY_ERROR_MESSAGES: Record<string, string> = {
  ANCHOR_NOT_FOUND: '历史已变化，未找到对应消息，请刷新后重试',
  INVALID_ANCHOR_ROLE: '历史消息角色不支持此操作',
  INVALID_BRANCH_REQUEST: '分支参数不合法，请刷新菜单后重试',
  SESSION_BUSY: '当前会话正在生成或压缩，请稍后再试',
  HISTORY_OPERATION_IN_PROGRESS: '当前会话正在执行其他历史操作',
  SUB_READONLY: '子会话不可操作',
  HISTORY_READONLY: '历史版本只读，不可重放',
  EMPTY_REPLAY_TEXT: '该消息没有可重放的文本',
  NO_PRIOR_USER: '未找到该回复所属的用户消息',
  REWRITE_ERROR: '历史重写未完成，请刷新确认并检查备份',
  TRUNCATE_COMMIT_ERROR: '历史截断提交状态不确定，请勿重复操作',
  BRANCH_COPY_ERROR: '分支复制失败，已回滚',
  BRANCH_ROLLBACK_FAILED: '分支清理失败，存在残留会话，请手动处理',
  REPLAY_SEND_FAILED: '历史已截断，但发送失败，可重试',
  REPLAY_TARGET_CHANGED: '会话已切换，历史已截断，本次未发送',
  IMAGE_MISSING: '部分图片已缺失，仅重放可用内容',
  TRUNCATE_ERROR: '截断失败',
  BRANCH_ERROR: '分支失败',
  MESSAGE_NOT_FOUND: '消息不存在或已变化，请刷新后重试',
  BACKUP_FAILED: '删除前备份失败，未删除任何数据，请检查磁盘或权限',
  DELETE_COMMIT_FAILED: '整轮删除提交失败，已回滚，请刷新后重试',
  DELETE_COMMIT_UNKNOWN: '提交状态不确定，请勿重复删除，请刷新对账',
  TURN_DELETE_UNSUPPORTED: '当前宿主不支持整轮删除，请升级后重试',
};

export function historyErrorMessage(code?: string | null, fallback = '历史操作失败'): string {
  if (!code) return fallback;
  return HISTORY_ERROR_MESSAGES[code] ?? fallback;
}

export function missingImageWarning(fileName?: string): { code: 'IMAGE_MISSING'; message: string } {
  return {
    code: 'IMAGE_MISSING',
    message: fileName ? `图片 ${fileName} 已缺失，未参与重放` : HISTORY_ERROR_MESSAGES['IMAGE_MISSING'],
  };
}

export type BridgeErrorCode =
  | 'BRIDGE_HANDSHAKE_FAILED'
  | 'BRIDGE_NOT_CONNECTED'
  | 'BRIDGE_VERSION_MISMATCH'
  | 'AGENT_START_ERROR'
  | 'ASK_UNSUPPORTED'
  | 'SUB_NOT_FOUND'
  | 'USAGE_UNSUPPORTED'
  | 'TURN_ERROR'
  | 'STREAM_ERROR'
  | 'COMPACT_FAILED'
  | 'SEND_FAILED'
  | 'BUDGET_FAILED'
  | 'REFRESH_FAILED'
  | 'UNKNOWN';

export interface BridgeError {
  code: BridgeErrorCode;
  message: string;
}

/** 统一失败体 {code,message} + toast + 可选重试。toast 走 naive 全局 $message，缺失时 console.warn。 */
export function toBridgeError(code: BridgeErrorCode, message: string): BridgeError {
  return { code, message: message || code };
}

export function reportBridgeError(err: BridgeError, retry?: () => void): void {
  try {
    console.warn(`[bridge] ${err.code}: ${err.message}`);
  } catch {
    /* console 不可用也不阻断 */
  }
  try {
    const w = window as unknown as {
      $message?: { error: (s: string) => void; warning: (s: string) => void };
      $notification?: { error: (o: { title: string; content: string }) => void };
    };
    if (retry && w.$notification) {
      w.$notification.error({ title: `桥接失败 [${err.code}]`, content: `${err.message}（可重试）` });
    } else {
      w.$message?.error?.(`[${err.code}] ${err.message}`);
    }
  } catch (e) {
    console.warn('[bridge] toast failed:', e);
  }
  void retry;
}

/** Date 序列化：CefSharp 把 C# DateTime 转成 JS Date，旧实现 Object.entries(Date) → {}。
 * 本函数深递归把 Date 还原为 ISO 字符串，保证往返契约始终是 string（proxy 唯一入口）。 */
export function serializeWithDates<T>(input: T): T {
  if (input instanceof Date) {
    try {
      return (Number.isNaN(input.getTime()) ? new Date().toISOString() : input.toISOString()) as unknown as T;
    } catch (e) {
      console.warn('[bridge] date serialize failed:', e);
      return new Date().toISOString() as unknown as T;
    }
  }
  if (input === null || input === undefined || typeof input !== 'object') return input;
  if (Array.isArray(input)) return (input as unknown[]).map((i) => serializeWithDates(i)) as unknown as T;
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(input as Record<string, unknown>)) {
    out[k] = serializeWithDates(v);
  }
  return out as unknown as T;
}

export function isBridgeError(v: unknown): v is BridgeError {
  if (!v || typeof v !== 'object') return false;
  const o = v as Record<string, unknown>;
  return typeof o['code'] === 'string' && typeof o['message'] === 'string';
}
