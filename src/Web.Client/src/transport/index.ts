// src/Web.Client/src/transport/index.ts
// D4-7 业务层唯一的传输入口。
//
// ============ P3 兼容说明 ============
//  本文件保留 P3 的 `createTransport()` / `Transport` / `TransportError` 导出
//  （BridgeTransport / MockTransport 类实现原文件不动）。
//  D4-8 起 store 只用下面的 `transport` 单例：
//    transport.isMock / .request / .pollEvents / .isReady / .host
// ============ 三层保证 ============
//  ① 业务代码只 import 这里的 `transport`，永远不知道自己在真/假环境
//  ② 真环境走 haloAgent / haloHost（经 agentBridge / hostBridge 安全封装）
//  ③ 假环境（纯浏览器 或 ?mock=1）走 bridge/mock 数据
// ============ ============
import { isMock as detectMock } from '@/bridge/detect';
import { hostBridge } from '@/bridge/host';
import { agentBridge } from '@/bridge/agent';
import { createMockAgent } from '@/bridge/mock/mockAgent';
import type { PollResult } from '@/bridge/types';

// ---- P3 兼容导出（实现文件原样保留） ----
import { MockTransport } from './MockTransport';
import { BridgeTransport } from './BridgeTransport';
export { MockTransport, BridgeTransport };
export type { Transport } from './types';
export { TransportError } from './types';
export { createTransport } from './legacy';

/**
 * 业务层唯一的传输入口（D4-7 起）。
 */
function shouldMock(): boolean {
  return detectMock();
}

const mockAgent = createMockAgent();

export const transport = {
  /** 是否在 Mock 模式（UI 可显示提示） */
  get isMock(): boolean {
    return shouldMock();
  },

  /** 引擎方法 */
  async request<T = unknown>(method: string, params: unknown = null): Promise<T> {
    if (shouldMock()) return mockAgent.request(method, params) as Promise<T>;
    return agentBridge.request<T>(method, params);
  },

  /** 拉事件 */
  async pollEvents(sinceSeq: number): Promise<PollResult> {
    if (shouldMock()) return mockAgent.pollEvents(sinceSeq);
    return agentBridge.pollEvents(sinceSeq);
  },

  /** 引擎是否就绪 */
  async isReady(): Promise<boolean> {
    if (shouldMock()) return mockAgent.isReady();
    return agentBridge.isReady();
  },

  /** 宿主接口（永远走 hostBridge，它内部会降级到 Mock） */
  host: hostBridge,
};

export type TransportFacade = typeof transport;
