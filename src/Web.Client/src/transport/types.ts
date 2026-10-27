import type { HaloMethod, HaloParams, HaloResult } from '@/contract/agent';
import type { EventFrame } from '@/contract/agent';

/** 连接状态机 */
export type ConnState =
  | { kind: 'connecting' }
  | { kind: 'ready'; version: string }
  | { kind: 'recovering'; message: string }
  | { kind: 'failed'; message: string; canRetry: boolean };

/** 事件拉取结果 */
export interface PollResult {
  frames: EventFrame[];
  nextSeq: number;
  hasGap: boolean;
  droppedEstimate: number;
}

/**
 * 传输抽象。
 *
 * 铁律：前端所有代码只依赖这个接口，
 *      绝不直接调用 window.hostBridge（除了 BridgeTransport 自己）。
 *
 * 这样 Mock 与真实通道可以无缝切换，UI 开发不需要后端。
 */
export interface Transport {
  readonly kind: 'mock' | 'bridge';

  /** 发起请求并等待应答。失败时抛 TransportError。 */
  invoke<M extends HaloMethod>(method: M, params?: HaloParams<M>): Promise<HaloResult<M>>;

  /** 拉取自 sinceSeq 之后的事件。 */
  pollEvents(sinceSeq: number, max?: number): Promise<PollResult>;

  /** 订阅连接状态变化。返回取消订阅函数。 */
  onConnectionChange(cb: (s: ConnState) => void): () => void;

  /** 销毁（断开连接 / 停止 mock 回放）。 */
  dispose(): void;
}

/** 带错误码的传输异常。前端可按 code 做差异化处理。 */
export class TransportError extends Error {
  constructor(
    public readonly code: string,
    message: string,
    public readonly hint?: string
  ) {
    super(message);
    this.name = 'TransportError';
  }
}
