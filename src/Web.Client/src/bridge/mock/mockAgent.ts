// src/Web.Client/src/bridge/mock/mockAgent.ts
// D4-7 无宿主时的假引擎（P07 已改为行为表驱动，见 behaviors.ts）。
import type { HaloFrame, PollResult } from '../types';
import { BEHAVIORS, mockAppendMessage } from './behaviors';

/**
 * 无宿主时的假引擎。
 *
 * ============ 设计要点 ============
 *  ① agent.prompt → turn.started → text.delta × N → turn.finished 流式
 *  ② 事件按 seq 递增，前端可以用 pollEvents 拉到“看起来真实”的流
 *  ③ P1 契约里的方法都有返回值（不返回 NotImplemented）
 *  ④ 参数校验宽松（前端传什么都能“成功”）
 * ============ 为什么事件要延迟产生 ============
 *  如果一次性产生所有事件，前端的流式渲染就测不出来。
 *  所以每条事件按 40–80ms 的间隔产生，模拟真实模型。
 * ============ P09-fix2 D ============
 *  会话历史由 behaviors.ts 的 Mock 会话存储持有（多会话、切走切回仍在）；
 *  这里只负责事件 seq 与流式回放，回放结束把助理回复落盘进当前 Mock 会话。
 * ============ ============
 */

let seqCounter = 0;
const events: HaloFrame[] = [];
let busy = false;
function emit(kind: string, payload: unknown): void {
  events.push({ t: 'evt', seq: ++seqCounter, k: kind, p: payload });
  // 限制内存
  if (events.length > 5000) events.splice(0, 1000);
}

function later(ms: number, fn: () => void): void {
  window.setTimeout(fn, ms);
}

const SAMPLE_REPLY = [
  '好的，我来帮你处理。',
  '',
  '我先看一下目录结构。',
  '',
  '### 分析结果',
  '',
  '- 找到 3 个文件',
  '- 其中 `config.json` 需要修改',
  '',
  '接下来我会：',
  '',
  '1. 读取 `config.json`',
  '2. 修改 `provider` 字段',
  '3. 保存',
  '',
  '完成。',
].join('\n');

/**
 * 模拟一个回合：
 *   turn.started → text.delta × N → turn.finished
 * 每隔约 60ms 推一块。
 */
function runMockTurn(): void {
  if (busy) return;
  busy = true;

  const turnId = `mock-turn-${Date.now()}`;
  emit('turn.started', { turnId });

  const chunks = chunkText(SAMPLE_REPLY, 6);
  let i = 0;

  const tick = () => {
    if (i >= chunks.length) {
      emit('turn.finished', { turnId, text: SAMPLE_REPLY, truncated: false });
      // P09-fix2 D：用户发言已在 agent.prompt 行为里落盘，这里只落助理回复。
      mockAppendMessage('assistant', SAMPLE_REPLY);
      busy = false;
      return;
    }
    emit('text.delta', { text: chunks[i]! });
    i++;
    later(40 + Math.random() * 40, tick);
  };

  later(120, tick);
}

function chunkText(s: string, size: number): string[] {
  const out: string[] = [];
  for (let i = 0; i < s.length; i += size) out.push(s.slice(i, i + size));
  return out;
}

export function createMockAgent() {
  return {
    async request(method: string, params: unknown): Promise<unknown> {
      // agent.prompt 保留流式副作用（用户发言落盘在 BEHAVIORS 里同步完成）
      if (method === 'agent.prompt') {
        runMockTurn();
      }
      if (method === 'agent.cancel') {
        emit('turn.finished', { turnId: 'mock', text: '', truncated: true, byUser: true });
        busy = false;
      }
      const b = BEHAVIORS[method];
      if (!b) {
        throw Object.assign(new Error(`method.notFound: 未知方法 ${method}（Mock 与契约不同步）`), {
          code: 'method.notFound',
        });
      }
      if (b.kind === 'throws') {
        throw Object.assign(new Error(b.message), { code: b.code });
      }
      return b.run(params);
    },

    async pollEvents(sinceSeq: number): Promise<PollResult> {
      const list = events.filter((e) => e.seq > sinceSeq);
      return {
        events: list.slice(0, 200),
        nextSeq: list.length > 0 ? list[Math.min(list.length, 200) - 1]!.seq : sinceSeq,
      };
    },

    async isReady(): Promise<boolean> {
      return true;
    },
  };
}

export type MockAgent = ReturnType<typeof createMockAgent>;

/** 仅开发用：手动推一条事件（画廊里测组件用） */
export function __emitMockEvent(kind: string, payload: unknown): void {
  emit(kind, payload);
}
