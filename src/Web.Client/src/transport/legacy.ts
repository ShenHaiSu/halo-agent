// src/Web.Client/src/transport/legacy.ts
// P3 的传输选择逻辑（原 transport/index.ts 的 createTransport），原样保留供兼容。
import { MockTransport } from './MockTransport';
import { BridgeTransport } from './BridgeTransport';
import type { Transport } from './types';

/**
 * 选择传输实现。
 *
 * 决策顺序：
 *   1. URL 带 ?mock=1 → 强制 Mock（便于浏览器直开调试）
 *   2. 宿主桥存在（CefSharp 存在）→ Bridge
 *   3. 都没有 → Mock（保证 pnpm dev 一定能用）
 */
export function createTransport(): Transport {
  const params = new URLSearchParams(location.search);
  if (params.get('mock') === '1') return new MockTransport();

  if (typeof window !== 'undefined' && typeof window.CefSharp !== 'undefined') {
    return new BridgeTransport();
  }

  // 浏览器直开：给一条明确提示，但功能照常可用（Mock）
  console.debug('[halo] 浏览器直开模式（使用 Mock 数据）');
  return new MockTransport();
}
