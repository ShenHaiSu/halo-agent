import type { HaloMethod, HaloParams, HaloResult } from '@/contract/agent'
import { TransportError, type ConnState, type PollResult, type Transport } from './types'

/**
 * 生产实现：通过 hostBridge（C# 侧）访问引擎。
 *
 * ============ 为什么用 poll 而不是 push ============
 *   ExecuteScriptAsync 在 CEF 里约 1ms/次。
 *   120 帧/秒的流式输出如果逐帧推送 = 12% CPU 全花在跨边界上。
 *   改成前端 getEventBatch 拉取，一次拿一批，开销可以忽略。
 *
 * ============ 形状约定 ============
 *   C# 侧的 HostBridge.GetEventBatchAsync(sinceSeq, max) 返回 JSON 字符串，
 *   前端解析。用字符串而不是对象，是因为 CefSharp 的异步桥对复杂泛型
 *   的序列化不稳定（嵌套数组含联合类型时容易丢字段）。
 */
export class BridgeTransport implements Transport {
  readonly kind = 'bridge' as const

  private connState: ConnState = { kind: 'connecting' }
  private listeners = new Set<(s: ConnState) => void>()
  private disposed = false
  private pollTimer: number | null = null
  private lastSeq = -1

  constructor() {
    void this.handshake()
    this.startPolling()
  }

  private async handshake() {
    try {
      await window.CefSharp?.BindObjectAsync('hostBridge')
      const info = await requireBridge().getSystemInfo()
      this.setConnState({ kind: 'ready', version: String(info.appVersion ?? '?') })
    } catch (e) {
      this.setConnState({
        kind: 'failed',
        message: `无法连接宿主：${String(e)}`,
        canRetry: false,
      })
    }
  }

  private startPolling() {
    const tick = async () => {
      if (this.disposed) return
      try {
        const raw = await requireBridge().getEventBatch(this.lastSeq, 500)
        const batch = JSON.parse(raw) as PollResult
        if (batch.frames.length > 0) {
          this.lastSeq = batch.nextSeq
          this.onBatch?.(batch)
        }
        if (batch.hasGap) {
          this.onGap?.(batch)
        }
      } catch {
        // 静默：下一次 tick 会重试；连接状态由宿主主动上报
      }
      this.pollTimer = window.requestAnimationFrame(() => void tick())
    }
    this.pollTimer = window.requestAnimationFrame(() => void tick())
  }

  /** 由上层（store）注入的批处理回调 */
  onBatch: ((b: PollResult) => void) | null = null
  onGap: ((b: PollResult) => void) | null = null

  async invoke<M extends HaloMethod>(
    method: M,
    params?: HaloParams<M>,
  ): Promise<HaloResult<M>> {
    if (this.disposed) throw new TransportError('disposed', '传输已销毁')

    const payload = params === undefined ? 'null' : JSON.stringify(params)
    const raw = await requireBridge().invoke(method, payload)

    let parsed: { ok: boolean; result?: unknown; error?: { code: string; msg: string; hint?: string } }
    try {
      parsed = JSON.parse(raw)
    } catch (e) {
      throw new TransportError('internal', `宿主返回了无法解析的应答：${String(e)}`)
    }

    if (!parsed.ok) {
      throw new TransportError(
        parsed.error?.code ?? 'internal',
        parsed.error?.msg ?? '未知错误',
        parsed.error?.hint,
      )
    }
    return parsed.result as HaloResult<M>
  }

  async pollEvents(_sinceSeq: number): Promise<PollResult> {
    // 生产实现由 startPolling 内部驱动；这个方法是给 Uniform API 用的
    const raw = await requireBridge().getEventBatch(this.lastSeq, 500)
    const batch = JSON.parse(raw) as PollResult
    if (batch.frames.length > 0) this.lastSeq = batch.nextSeq
    return batch
  }

  onConnectionChange(cb: (s: ConnState) => void): () => void {
    this.listeners.add(cb)
    cb(this.connState)
    return () => this.listeners.delete(cb)
  }

  /** 由 HostBridge 事件调用（宿主通过 evaluateScript 推送状态变化） */
  handleConnState(state: ConnState) {
    this.setConnState(state)
  }

  private setConnState(s: ConnState) {
    this.connState = s
    for (const cb of this.listeners) cb(s)
  }

  dispose(): void {
    this.disposed = true
    if (this.pollTimer !== null) {
      cancelAnimationFrame(this.pollTimer)
      this.pollTimer = null
    }
    this.listeners.clear()
  }
}

// ---- 宿主桥访问（D1-4 的 HostBridge 提供 invoke / getEventBatch） ----
//
// 注意：main.ts 里已有一份基于真机探针的 Window.hostBridge 声明
// （getSystemInfoAsync / enginePingAsync，全部可选）。
// 这里不重复声明全局类型，而是运行时取桥并断言为完整形态：
// D1-4 落地后宿主会补齐 invoke / getEventBatch，Mock 模式下根本走不到这里。
interface HostBridgeApi {
  getSystemInfo(): Promise<Record<string, unknown>>
  invoke(method: string, paramsJson: string): Promise<string>
  getEventBatch(sinceSeq: number, max: number): Promise<string>
  [key: string]: unknown
}

function requireBridge(): HostBridgeApi {
  const raw = (window as unknown as { hostBridge?: unknown }).hostBridge
  if (!raw) throw new TransportError('disposed', '宿主桥不存在（可能不在 CefSharp 环境内）')
  return raw as HostBridgeApi
}
