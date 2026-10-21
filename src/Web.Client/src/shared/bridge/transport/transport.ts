/** need21-F02: 桥传输抽象（Cef 与 Mock 同接口；api 层只依赖本接口，不触原生对象）。 */
export interface BridgeTransport {
  /** 按方法名直调原生/模拟桥（参数原样透传）。缺失方法抛 {code:'BRIDGE_NOT_CONNECTED'}。 */
  call<T>(method: string, ...args: unknown[]): Promise<T>;
  /** 方法是否存在（老后端缺方法降级空态用，不抛）。 */
  supports(method: string): boolean;
  /** 后端协议版本；无版本接口返回 null（legacy，由 assertBridgeVersion 消化）。 */
  version(): Promise<number | null>;
}
