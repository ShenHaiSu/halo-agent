/** need21-F02: Cef 真实传输（旧 client.ts negotiate 版本探测语义搬入；调用直透原生对象）。 */
import type { BridgeTransport } from './transport';

export class CefTransport implements BridgeTransport {
  constructor(private readonly raw: unknown) {}

  async call<T>(method: string, ...args: unknown[]): Promise<T> {
    const fn = (this.raw as Record<string, unknown> | null | undefined)?.[method];
    if (typeof fn !== 'function') {
      const err = { code: 'BRIDGE_NOT_CONNECTED', message: `桥接方法缺失：${method}` };
      throw Object.assign(new Error(err.message), err);
    }
    return (await (fn as (...a: unknown[]) => Promise<T>).apply(this.raw, args)) as T;
  }

  supports(method: string): boolean {
    return typeof (this.raw as Record<string, unknown> | null | undefined)?.[method] === 'function';
  }

  async version(): Promise<number | null> {
    const raw = this.raw as Record<string, unknown> | null | undefined;
    const getVersion = raw?.['getBridgeVersion'] ?? raw?.['getVersion'];
    if (typeof getVersion !== 'function') return null;
    const v = await (getVersion as () => Promise<unknown>).call(this.raw);
    return typeof v === 'number' ? v : Number((v as { version?: unknown })?.version ?? v);
  }
}
