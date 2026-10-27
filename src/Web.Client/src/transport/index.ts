import { BridgeTransport } from './BridgeTransport'
import { MockTransport } from './MockTransport'
import type { Transport } from './types'

/**
 * 选择传输实现。
 *
 * 决策顺序：
 *   1. URL 带 ?mock=1 → 强制 Mock（便于浏览器直开调试）
 *   2. HALO_MOCK 环境变量 → Mock
 *   3. 宿主桥存在（CefSharp 存在）→ Bridge
 *   4. 都没有 → Mock（保证 pnpm dev 一定能用）
 */
export function createTransport(): Transport {
  const params = new URLSearchParams(location.search)
  if (params.get('mock') === '1') return new MockTransport()

  if (typeof window !== 'undefined' && typeof window.CefSharp !== 'undefined') {
    return new BridgeTransport()
  }

  // 浏览器直开：给一条明确提示，但功能照常可用（Mock）
  console.info('[halo] 浏览器直开模式（使用 Mock 数据）')
  return new MockTransport()
}

export type { Transport } from './types'
export { TransportError } from './types'
