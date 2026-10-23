/** need21-F02: 桥协议错误码 + 上报 + Date 序列化（旧 src/bridge/types.ts 原样迁移，行为不变）。 */

/** Mock 显式横幅文案（红色横幅 + 重试按钮，禁止静默 Mock）。 */
export const MOCK_BANNER_TEXT = '桥接未连接（Mock数据）';

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
