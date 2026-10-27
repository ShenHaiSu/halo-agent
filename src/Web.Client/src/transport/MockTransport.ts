import type { HaloMethod, HaloParams, HaloResult, EventFrame } from '@/contract/agent'
import type { StateSnapshot } from '@/contract/agent'
import { TransportError, type ConnState, type PollResult, type Transport } from './types'
import { getScenario, type Scenario } from './mock/scenarios'
import { buildEmptySnapshot } from './mock/snapshot'

/**
 * Mock 传输：模拟引擎行为，用于前端独立开发。
 *
 * 不是"返回假数据"，而是**回放一个真实的事件序列**：
 *   - 输入提示词 → 按场景回放 turn-*.ndjson
 *   - 事件带真实时间间隔（模拟流式）
 *   - 支持 agent.answer（权限询问的通过与拒绝两个分支）
 *
 * 场景由 fixtures/turn-*.ndjson 定义（每行一个 EventFrame JSON）。
 */
export class MockTransport implements Transport {
  readonly kind = 'mock' as const

  private snapshot: StateSnapshot = buildEmptySnapshot()
  private frames: EventFrame[] = []
  private seq = 0
  private connState: ConnState = { kind: 'ready', version: 'mock-0.1.0' }
  private listeners = new Set<(s: ConnState) => void>()
  private disposed = false
  private replayTimers: number[] = []
  private pendingAskResolve: ((allow: boolean, always: boolean) => void) | null = null

  constructor(private readonly opts: { speed?: number } = {}) {}

  async invoke<M extends HaloMethod>(
    method: M,
    params?: HaloParams<M>,
  ): Promise<HaloResult<M>> {
    if (this.disposed) throw new TransportError('disposed', '传输已销毁')
    await delay(30)   // 模拟一点网络延迟，让 loading 状态可见

    switch (method) {
      case 'system.ping':
        return { pong: true, version: 'mock', protocol: 1, pid: 9999 } as HaloResult<M>

      case 'state.snapshot':
      case 'agent.resync':
        return this.snapshot as unknown as HaloResult<M>

      case 'agent.prompt': {
        const p = params as unknown as { text: string }
        this.startReplay(p.text)
        return { accepted: true, turnId: 'mock-turn-1' } as unknown as HaloResult<M>
      }

      case 'agent.answer': {
        const p = params as unknown as { askId: string; allow: boolean; always: boolean }
        if (!this.pendingAskResolve) {
          return { accepted: false, reason: 'no-pending' } as unknown as HaloResult<M>
        }
        const resolve = this.pendingAskResolve
        this.pendingAskResolve = null
        resolve(p.allow, p.always)
        return { accepted: true, reason: null } as unknown as HaloResult<M>
      }

      case 'agent.cancel':
        this.stopReplay()
        return { cancelled: true } as unknown as HaloResult<M>

      case 'state.setMode': {
        const p = params as unknown as { mode: NonNullable<StateSnapshot['agent']>['mode'] }
        const labels: Record<string, string> = {
          ask: '询问',
          acceptEdits: '编辑放行',
          bypassPermissions: '全自动',
          yolo: '超级',
        }
        const mode = p.mode ?? 'ask'
        if (this.snapshot.agent) {
          this.snapshot.agent.mode = mode
          this.snapshot.agent.modeLabel = labels[mode] ?? '询问'
        }
        return { mode, label: this.snapshot.agent?.modeLabel ?? '询问' } as unknown as HaloResult<M>
      }

      case 'session.list':
        return { items: MOCK_SESSIONS } as unknown as HaloResult<M>

      case 'tool.list':
        return { items: MOCK_TOOLS } as unknown as HaloResult<M>

      default:
        // 其余方法返回最小可用 stub，避免前端开发被阻断
        return {} as unknown as HaloResult<M>
    }
  }

  async pollEvents(sinceSeq: number, max = 500): Promise<PollResult> {
    const frames = this.frames.filter((f) => f.seq > sinceSeq).slice(0, max)
    const nextSeq = frames.length > 0 ? frames[frames.length - 1].seq + 1 : sinceSeq
    return { frames, nextSeq, hasGap: false, droppedEstimate: 0 }
  }

  onConnectionChange(cb: (s: ConnState) => void): () => void {
    this.listeners.add(cb)
    cb(this.connState)
    return () => this.listeners.delete(cb)
  }

  // ---------------- 回放 ----------------

  private startReplay(userText: string) {
    this.stopReplay()

    // 把用户消息写进快照
    if (this.snapshot.messages) {
      this.snapshot.messages.push({
        id: `m-u-${Date.now()}`,
        role: 'user',
        text: userText,
        tsMs: Date.now(),
      })
    }
    if (this.snapshot.agent) this.snapshot.agent.busy = true
    this.pushEvent('turn.started', { turnId: 'mock-turn-1' })
    this.pushEvent('state.busyChanged', { busy: true, queueLength: 0 })

    const scenario = getScenario(userText)
    this.scheduleScenario(scenario)
  }

  private scheduleScenario(s: Scenario) {
    let acc = 0
    const speed = this.opts.speed ?? 1
    for (const step of s.steps) {
      acc += step.afterMs / speed
      const timer = window.setTimeout(() => {
        if (this.disposed) return
        this.pushEvent(step.kind, step.payload)

        if (step.kind === 'ask.permission') {
          // 暂停回放，等用户回答
          this.pendingAskResolve = (allow) => {
            this.snapshot.pendingAsk = null
            if (allow) {
              this.pushEvent('notice', { text: '✔ 已允许', level: 'info' })
              this.scheduleScenario(s.onAllow ?? { steps: [] })
            } else {
              this.pushEvent('notice', { text: '✘ 已拒绝', level: 'warn' })
              this.scheduleScenario(s.onDeny ?? { steps: [] })
            }
          }
        }

        if (step.kind === 'turn.finished') {
          if (this.snapshot.agent) this.snapshot.agent.busy = false
          this.pushEvent('state.busyChanged', { busy: false, queueLength: 0 })
        }
      }, acc)
      this.replayTimers.push(timer)
    }
  }

  private stopReplay() {
    for (const t of this.replayTimers) clearTimeout(t)
    this.replayTimers = []
    this.pendingAskResolve = null
  }

  private pushEvent(kind: string, payload: unknown) {
    const frame: EventFrame = {
      t: 'evt',
      seq: this.seq++,
      k: kind,
      p: payload as Record<string, unknown>,
    }
    this.frames.push(frame)

    // 同步更新快照（Mock 也需要"状态一致"）
    this.applyToSnapshot(kind, payload)
  }

  private applyToSnapshot(kind: string, payload: unknown) {
    const p = payload as Record<string, unknown>
    switch (kind) {
      case 'turn.finished':
        if (this.snapshot.agent) this.snapshot.agent.busy = false
        break
      case 'ask.permission':
      case 'ask.rounds':
        this.snapshot.pendingAsk = p as unknown as StateSnapshot['pendingAsk']
        break
      case 'usage':
        if (this.snapshot.usage) {
          this.snapshot.usage.inputTotal = (this.snapshot.usage.inputTotal ?? 0) + Number(p.prompt ?? 0)
          this.snapshot.usage.outputTotal = (this.snapshot.usage.outputTotal ?? 0) + Number(p.completion ?? 0)
          this.snapshot.usage.lastPromptTokens = Number(p.prompt ?? 0)
        }
        break
      case 'round.started':
        if (this.snapshot.agent) {
          this.snapshot.agent.round = {
            used: Number(p.used ?? 0),
            limit: Number(p.limit ?? 0),
          }
        }
        break
      case 'state.personaChanged':
        if (this.snapshot.agent) this.snapshot.agent.persona = String(p.name ?? '')
        break
    }
  }

  dispose(): void {
    this.disposed = true
    this.stopReplay()
    this.listeners.clear()
  }
}

function delay(ms: number) {
  return new Promise((r) => setTimeout(r, ms))
}

const MOCK_SESSIONS = [
  {
    id: 's-mock-1',
    title: '把 Downloads 里的 zip 归档',
    note: '示例会话',
    cwd: 'C:\\work\\proj',
    headless: false,
    mtimeMs: Date.now() - 3600_000,
    userMsgs: 3,
    assistantMsgs: 3,
  },
  {
    id: 's-mock-2',
    title: '排查测试失败原因',
    note: null,
    cwd: 'C:\\work\\proj',
    headless: false,
    mtimeMs: Date.now() - 86400_000,
    userMsgs: 8,
    assistantMsgs: 9,
  },
]

const MOCK_TOOLS = [
  { name: 'list_directory', description: '列出目录下的条目', source: 'builtin' },
  { name: 'read_file', description: '读取文本文件内容', source: 'builtin' },
  { name: 'write_file', description: '创建或覆写文件', source: 'builtin' },
  { name: 'edit', description: '精确替换文件中的文本', source: 'builtin' },
  { name: 'glob', description: '按模式查找文件', source: 'builtin' },
  { name: 'grep_search', description: '按正则搜索文本内容', source: 'builtin' },
  { name: 'run_shell_command', description: '执行终端命令', source: 'builtin' },
  { name: 'memory_write', description: '写入长期记忆', source: 'builtin' },
  { name: 'memory_read', description: '读取长期记忆', source: 'builtin' },
  { name: 'web_fetch', description: '抓取网页并转为文本', source: 'builtin' },
]
