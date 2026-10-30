// P12-02 回归：state.snapshot 全量同步 session.agent（模型切换器 unconfigured 恒 true）。
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { useMessagesStore } from './messages';
import { useSessionStore } from './session';
import { useSessionsStore } from './sessions';
import { transport } from '@/transport';
import { setMockAgentConfigured } from '@/bridge/mock/behaviors';

const msgView = (id: string, text: string) => ({ id, role: 'assistant', text, tsMs: 1 });

const snapConfigured = (id: string) => ({
  session: { id, cwd: '(mock)', historyPath: `(mock)/${id}` },
  agent: { provider: 'deepseek', providerDisplay: 'DeepSeek', model: 'deepseek-chat', modelDisplay: 'deepseek-chat', configured: true },
  usage: { inputTotal: 10, outputTotal: 20 },
  messages: [msgView('m1', 'hi')],
});

beforeEach(() => {
  setActivePinia(createPinia());
  vi.restoreAllMocks();
  setMockAgentConfigured(true);
});

describe('P12-02: 快照同步模型状态', () => {
  it('loadSnapshot 含 agent 时 session 被置位（含 usage 映射）', async () => {
    const messages = useMessagesStore();
    const session = useSessionStore();
    expect(session.configured).toBe(false);
    vi.spyOn(transport, 'request').mockResolvedValue(snapConfigured('s-1') as never);
    await messages.loadSnapshot();
    expect(session.configured).toBe(true);
    expect(session.provider).toBe('deepseek');
    expect(session.model).toBe('deepseek-chat');
    expect(session.usage.input).toBe(10);
    expect(session.usage.output).toBe(20);
    expect(messages.items.map((m) => m.content)).toEqual(['hi']);
  });

  it('select 经 resume 快绘后仍补 agent（token 门控内）', async () => {
    const sessions = useSessionsStore();
    const session = useSessionStore();
    sessions.items = [{ id: 'A', title: 'A', mtimeMs: 1, headless: false }] as never;
    sessions.currentId = '';
    vi.spyOn(transport, 'request').mockImplementation(async (method: string) => {
      if (method === 'session.resume') return { count: 1, messages: [msgView('mA', 'A 历史')] } as never;
      if (method === 'state.snapshot') return snapConfigured('A') as never;
      throw new Error(`unexpected ${method}`);
    });
    await sessions.select('A');
    expect(sessions.currentId).toBe('A');
    expect(session.configured).toBe(true);
    expect(session.provider).toBe('deepseek');
  });

  it('agent 缺席时不覆盖已有 session（后兼容）', async () => {
    const messages = useMessagesStore();
    const session = useSessionStore();
    session.applySnapshotAgent({ provider: 'p0', model: 'm0', configured: true });
    expect(session.configured).toBe(true);
    vi.spyOn(transport, 'request').mockResolvedValue({ messages: [msgView('m1', 'x')] } as never);
    await messages.loadSnapshot();
    expect(session.configured).toBe(true);
    expect(session.provider).toBe('p0');
  });

  it('mock 未配置分支可切 configured:false', async () => {
    setMockAgentConfigured(false);
    const messages = useMessagesStore();
    const session = useSessionStore();
    const { BEHAVIORS } = await import('@/bridge/mock/behaviors');
    const snap = BEHAVIORS['state.snapshot'];
    const raw = (snap as { kind: string; run: () => unknown }).run() as { agent: { configured: boolean; provider: string; model: string } };
    expect(raw.agent.configured).toBe(false);
    vi.spyOn(transport, 'request').mockResolvedValue(raw as never);
    await messages.loadSnapshot();
    expect(session.configured).toBe(false);
    expect(session.provider).toBe('');
    expect(session.model).toBe('');
  });
});
