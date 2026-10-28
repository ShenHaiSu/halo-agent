// src/Web.Client/src/bridge/agent.ts
// D4-7 window.haloAgent 的安全封装（3 方法）。
import type { HaloAgent, HaloFrame, PollResult } from './types';
import { hasAgent } from './bind';

/**
 * window.haloAgent 的安全封装。
 *
 * ============ 为什么这里要 JSON.parse ============
 *  C# 桥（AgentBridge.Request/PollEvents）返回的一律是 JSON 字符串
 * （dist 骨架屏根因修复）：JsonElement / DTO 绝不能直接过 CefSharp 桥——
 *  复杂返回值的桥编组不稳定（嵌套丢字段）且 DTO 按 C# 原名（PascalCase）返回，
 *  而前端全按 camelCase 消费，直接返回会导致数据静默全空。
 *  异常仍走 throw（CefSharp 会 reject JS Promise，这里包成可读错误）。
 * ============ Mock 在哪 ============
 *  Mock 的实现在 transport 层（`transport/index.ts` 判断走真走假）。
 *  这一层只负责"真环境下的安全调用"。
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

/** 桥返回的 JSON 文本还原为值；非字符串则兼容直传（并 warn，方便排查契约漂移）。 */
function parseBridgeJson(raw: unknown, what: string): unknown {
  if (typeof raw !== 'string') {
    console.warn(`[bridge] ${what} 返回的不是字符串，已直接采用`, raw);
    return raw;
  }
  try {
    return JSON.parse(raw) as unknown;
  } catch (e) {
    throw new Error(`引擎返回了无法解析的应答（${what}）：${describeError(e)}`);
  }
}

export const agentBridge = {
  async request<T = unknown>(method: string, params: unknown): Promise<T> {
    if (!hasAgent()) throw new Error('引擎桥接不可用');
    let raw: string;
    try {
      raw = await requireAgent().request(method, params);
    } catch (e) {
      // 保留引擎返回的业务错误文本（前端需要原文显示）
      throw new Error(describeError(e));
    }
    return parseBridgeJson(raw, method) as T;
  },

  async pollEvents(sinceSeq: number): Promise<PollResult> {
    if (!hasAgent()) throw new Error('引擎桥接不可用（window.haloAgent 不存在）');
    let parsed: unknown;
    try {
      const raw = await requireAgent().pollEvents(sinceSeq);
      parsed = parseBridgeJson(raw, 'pollEvents');
    } catch (e) {
      // 事件通道失败/解析失败不断链：记 warn 并给安全默认，下一次 tick 重试。
      console.warn('[bridge] pollEvents 失败，已用空批代替', e);
      return { events: [], nextSeq: sinceSeq };
    }
    const r = (parsed ?? {}) as { events?: unknown; nextSeq?: unknown };
    // 防御：桥接返回形状不对时给一个安全默认（之前这里是静默吞事件的黑洞）
    if (!r || !Array.isArray(r.events)) {
      console.warn('[bridge] pollEvents 形状异常，已用空批代替', parsed);
      return { events: [], nextSeq: sinceSeq };
    }
    return {
      events: r.events as HaloFrame[],
      nextSeq: typeof r.nextSeq === 'number' ? r.nextSeq : sinceSeq,
    };
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
