/** need22-P3 G5：桥接入口代理（四模块之 proxy，negotiate 入口）。
 * - negotiate()：Bind → 版本握手 → 选 client/mock。握手失败直接抛，不静默。
 * - 失败统一 {code,message} + toast + 重试（reportBridgeError + withBridgeRetry）。
 * - Date 序列化、startAgentTurn 参数收敛经由 client.ts，业务代码不再 try/catch 兼容。 */
import { getHostBridge, initHostBridge, type HostBridgeApi } from './hostBridge';
import { negotiate as negotiateVersion } from './client';
import { setMockMode, isMockMode, shouldShowMockBanner, MOCK_BANNER_TEXT } from './mock';
import { reportBridgeError, toBridgeError, type BridgeError } from './types';

export { MOCK_BANNER_TEXT, shouldShowMockBanner, isMockMode, setMockMode };
export type { BridgeError, HostBridgeApi };

export interface NegotiateResult {
  mode: 'native' | 'mock';
  version: number | null;
}

/**
 * 握手入口：init 桥接 → 有原生对象则版本协商 → 成功走 client，失败显式 Mock + 抛错。
 * 约定：Mock 绝不静默——设置 Mock 标记（横幅）并向调用方抛 BRIDGE_NOT_CONNECTED，由 main/调用方展示横幅 + 重试。
 */
export async function negotiate(): Promise<NegotiateResult> {
   const w = window as unknown as { CefSharp?: { BindObjectAsync: (n: string) => Promise<void> }; hostBridge?: unknown };
  // 1) Bind
  if (w.CefSharp && !w.hostBridge) {
    try {
      await w.CefSharp.BindObjectAsync('hostBridge');
    } catch (e) {
      const err = toBridgeError('BRIDGE_NOT_CONNECTED', `BindObjectAsync 失败：${e instanceof Error ? e.message : String(e)}`);
      reportBridgeError(err);
      setMockMode(true);
      await initHostBridge();
      throw Object.assign(new Error(err.message), err);
    }
  }
  // 2) 版本握手（有原生对象才谈版本）
  if (w.hostBridge) {
    const { version } = await negotiateVersion(w.hostBridge);
    setMockMode(false);
    return { mode: 'native', version };
  }
  // 3) 无原生对象：显式 Mock（横幅），并抛错告知调用方
  setMockMode(true);
  await initHostBridge();
  const err = toBridgeError('BRIDGE_NOT_CONNECTED', `${MOCK_BANNER_TEXT}：未检测到 CefSharp hostBridge，已使用 Mock 数据`);
  reportBridgeError(err);
  throw Object.assign(new Error(err.message), err);
}

/** 业务统一取桥：附带 mock 标记，调用方据此展示横幅/禁用联调断言。 */
export function getProxiedBridge(): { bridge: HostBridgeApi; mock: boolean } {
  return { bridge: getHostBridge(), mock: isMockMode() };
}

/** 统一重试包装：失败转 {code,message} + toast，调用方传 retry 回调即带重试按钮。 */
export async function withBridgeRetry<T>(
  label: string,
  fn: () => Promise<T>,
  opts?: { retry?: () => void },
): Promise<T> {
  try {
    return await fn();
  } catch (e) {
    if (e instanceof Error && (e as Error & { code?: string }).code) throw e;
    const err = toBridgeError('UNKNOWN', `${label}失败：${e instanceof Error ? e.message : String(e)}`);
    reportBridgeError(err, opts?.retry);
    throw Object.assign(new Error(err.message), err);
  }
}
