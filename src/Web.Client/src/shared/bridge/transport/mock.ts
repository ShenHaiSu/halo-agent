/** need21-F02: Mock 传输（包 @/mocks/*，仅 import type；实现由 hostBridge 在 DEV&&?mock 时动态加载注入）。 */
import { BRIDGE_VERSION } from '../protocol/version';
import type { HostBridgeApi } from '../protocol/types';
import type { BridgeTransport } from './transport';

export class MockTransport implements BridgeTransport {
  constructor(private readonly mockApi: HostBridgeApi) {}

  async call<T>(method: string, ...args: unknown[]): Promise<T> {
    const fn = (this.mockApi as unknown as Record<string, unknown>)[method];
    if (typeof fn !== 'function') {
      const err = { code: 'BRIDGE_NOT_CONNECTED', message: `Mock 桥接方法缺失：${method}` };
      throw Object.assign(new Error(err.message), err);
    }
    return (await (fn as (...a: unknown[]) => Promise<T>).apply(this.mockApi, args)) as T;
  }

  supports(method: string): boolean {
    return typeof (this.mockApi as unknown as Record<string, unknown>)[method] === 'function';
  }

  async version(): Promise<number | null> {
    return BRIDGE_VERSION;
  }
}
