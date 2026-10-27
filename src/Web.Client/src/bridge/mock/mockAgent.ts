// src/Web.Client/src/bridge/mock/mockAgent.ts
// D4-7 无宿主时的假引擎（按 P1 契约返回假数据）。
import type { HaloFrame, PollResult } from '../types';

/**
 * 无宿主时的假引擎。
 *
 * ============ 设计要点 ============
 *  ① agent.prompt → turn.started → text.delta × N → turn.finished 流式
 *  ② 事件按 seq 递增，前端可以用 pollEvents 拉到"看起来真实"的流
 *  ③ P1 契约里的方法都有返回值（不返回 NotImplemented）
 *  ④ 参数校验宽松（前端传什么都能"成功"）
 * ============ 为什么事件要延迟产生 ============
 *  如果一次性产生所有事件，前端的流式渲染就测不出来。
 *  所以每条事件按 40–80ms 的间隔产生，模拟真实模型。
 * ============ ============
 */

interface MockSession {
  messages: Array<{ role: string; content: string }>;
}

let seqCounter = 0;
const events: HaloFrame[] = [];
const sessions: MockSession[] = [{ messages: [] }];
let currentSession = 0;
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
function runMockTurn(prompt: string): void {
  if (busy) return;
  busy = true;

  const turnId = `mock-turn-${Date.now()}`;
  emit('turn.started', { turnId });

  const chunks = chunkText(SAMPLE_REPLY, 6);
  let i = 0;

  const tick = () => {
    if (i >= chunks.length) {
      emit('turn.finished', { turnId, text: SAMPLE_REPLY, truncated: false });
      sessions[currentSession]!.messages.push(
        { role: 'user', content: prompt },
        { role: 'assistant', content: SAMPLE_REPLY }
      );
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

const MOCK_SESSIONS_LIST = [
  {
    id: 's-mock-1',
    title: '整理 Downloads 里的安装包',
    mtime: Date.now() - 1000 * 60 * 30,
    headless: false,
  },
  {
    id: 's-mock-2',
    title: '排查 build 失败原因',
    mtime: Date.now() - 1000 * 60 * 60 * 5,
    headless: false,
  },
  {
    id: 's-mock-3',
    title: '生成项目 README',
    mtime: Date.now() - 1000 * 60 * 60 * 26,
    headless: true,
  },
];

export function createMockAgent() {
  return {
    async request(method: string, params: unknown): Promise<unknown> {
      switch (method) {
        case 'system.ping':
          return { pong: true, uptimeMs: 12345 };

        case 'system.info':
          return {
            engineVersion: '0.4.0-mock',
            protocolVersion: 1,
            platform: 'mock',
          };

        case 'system.protocol':
          return { version: 1, methods: [], events: [], errorCodes: [] };

        case 'agent.prompt': {
          const p = params as { text?: string } | null;
          runMockTurn(p?.text ?? '(空)');
          return { accepted: true, turnId: `mock-turn-${Date.now()}` };
        }

        case 'agent.queue':
          return { queued: true };

        case 'agent.unqueue':
          return { removed: true };

        case 'agent.cancel':
          emit('turn.finished', { turnId: 'mock', text: '', truncated: true, byUser: true });
          busy = false;
          return { cancelled: true };

        case 'agent.answer':
          return { ok: true };

        case 'agent.drain':
          return { drained: 0 };

        case 'agent.resync':
        case 'state.snapshot':
          return {
            seq: seqCounter,
            sessionId: MOCK_SESSIONS_LIST[currentSession]?.id ?? 's-mock-1',
            messages: sessions[currentSession]!.messages,
            cwd: '(mock) C:\\work',
            mode: 'ask',
            persona: '',
            maxTurns: 200,
            retry: 0,
            busy,
            usage: { input: 0, output: 0 },
          };

        case 'session.new':
          sessions.push({ messages: [] });
          currentSession = sessions.length - 1;
          return { sessionId: `s-mock-${Date.now()}` };

        case 'session.resume':
          return { ok: true };

        case 'session.list':
          return MOCK_SESSIONS_LIST;

        case 'session.rename':
          return { ok: true };

        case 'session.delete':
          return { ok: true };

        case 'session.clear':
          sessions[currentSession]!.messages.length = 0;
          return { ok: true };

        case 'session.compact':
          return { removed: 0, summary: '' };

        case 'session.setCwd':
          return { cwd: (params as { path?: string } | null)?.path ?? '' };

        case 'state.setMode':
          return { mode: (params as { mode?: string } | null)?.mode ?? 'ask' };

        case 'state.setPersona':
          return { persona: (params as { name?: string } | null)?.name ?? '' };

        case 'state.setMaxTurns':
          return { maxTurns: (params as { value?: number } | null)?.value ?? 0 };

        case 'state.setRetry':
          return { retry: (params as { value?: number } | null)?.value ?? 0 };

        case 'tool.list':
          return [
            { name: 'list_directory', category: 'read' },
            { name: 'read_file', category: 'read' },
            { name: 'write_file', category: 'write' },
            { name: 'edit', category: 'write' },
            { name: 'glob', category: 'read' },
            { name: 'grep_search', category: 'read' },
            { name: 'run_shell_command', category: 'command' },
            { name: 'memory_write', category: 'read' },
            { name: 'memory_read', category: 'read' },
            { name: 'web_fetch', category: 'read' },
          ];

        case 'tool.undoList':
          return [];

        case 'tool.undoRollback':
          return { ok: true };

        case 'settings.get':
          return {
            provider: 'deepseek',
            providers: {},
            maxTurns: 200,
            persona: '',
            proxy: { mode: 'auto' },
            retry: 0,
          };

        case 'settings.set':
          return { ok: true };

        case 'settings.testProvider':
          return { ok: true, models: ['deepseek-chat', 'deepseek-reasoner'] };

        case 'settings.listProviders':
          return [
            { name: 'ollama', displayName: 'Ollama（本地）' },
            { name: 'dashscope', displayName: '阿里云百炼' },
            { name: 'deepseek', displayName: 'DeepSeek' },
            { name: 'openrouter', displayName: 'OpenRouter' },
            { name: 'zai', displayName: 'Z.AI' },
          ];

        case 'asset.listThemes':
          return [];

        case 'asset.readTheme':
          throw new Error('Mock 环境无用户主题');

        case 'asset.listSkills':
          return [];

        case 'asset.listPersonas':
          return [];

        case 'update.check':
          return { configured: false, message: '未配置更新源' };

        case 'update.download':
          return { configured: false };

        case 'update.apply':
          return { configured: false };

        default:
          // 未知方法返回一个空对象而不是抛错（便于前端开发时先走通）
          console.warn(`[mockAgent] 未实现的方法：${method}`);
          return {};
      }
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
