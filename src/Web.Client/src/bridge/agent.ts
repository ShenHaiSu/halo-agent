// src/Web.Client/src/bridge/agent.ts
// D4-7 window.haloAgent 的安全封装（3 方法）。
import type { HaloAgent, PollResult } from './types';
import { hasAgent } from './bind';

/**
 * window.haloAgent 的安全封装。
 *
 * ============ 为什么这里不做 Mock ============
 *  Mock 的实现在 transport 层（`transport/index.ts` 判断走真走假）。
 *  这一层只负责"真环境下的安全调用"：
 *    ① 桥接不存在时给明确错误 / 安全默认
 *    ② 把 CefSharp 的异常包装成可读信息
 * ============ ============
 */
function requireAgent(): HaloAgent {
  const a = window.haloAgent;
  if (!a || typeof a.request !== 'function') {
    throw new Error('引擎桥接不可用（window.haloAgent 不存在）');
  }
  return a;
}

function describeError(e: unknown): string {
  if (e instanceof Error) return e.message;
  if (typeof e === 'string') return e;
  if (e && typeof e === 'object' && 'message' in e) {
    return String((e as { message: unknown }).message);
  }
  return String(e);
}

export const agentBridge = {
  async request<T = unknown>(method: string, params: unknown): Promise<T> {
    if (!hasAgent()) throw new Error('引擎桥接不可用');
    try {
      return (await requireAgent().request(method, params)) as T;
    } catch (e) {
      // 保留引擎返回的业务错误文本（前端需要原文显示）
      throw new Error(describeError(e));
    }
  },

  async pollEvents(sinceSeq: number): Promise<PollResult> {
    if (!hasAgent()) return { events: [], nextSeq: sinceSeq };
    try {
      const r = await requireAgent().pollEvents(sinceSeq);
      // 防御：桥接返回 undefined 时给一个安全默认
      if (!r || !Array.isArray(r.events)) return { events: [], nextSeq: sinceSeq };
      return r;
    } catch (e) {
      // 拉事件失败不应抛到上层（pump 会重试）
      console.warn('[agent] pollEvents 失败', describeError(e));
      return { events: [], nextSeq: sinceSeq };
    }
  },

  async isReady(): Promise<boolean> {
    if (!hasAgent()) return false;
    try {
      return await requireAgent().isReady();
    } catch {
      return false;
    }
  },
};

export type AgentBridge = typeof agentBridge;
