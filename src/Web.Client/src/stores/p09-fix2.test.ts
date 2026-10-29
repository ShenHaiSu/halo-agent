// P09-fix2 回归：lastSeq 只增不减 / select 切换令牌 / 切换中过期帧丢弃 / Mock 多会话。
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { useMessagesStore } from './messages';
import { useSessionsStore } from './sessions';
import { dispatchFrames } from './eventBridge';
import { transport } from '@/transport';
import { BEHAVIORS } from '@/bridge/mock/behaviors';

function deferred<T>() {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const listFixture = () => [
  { id: 'A', title: 'A', note: null, cwd: null, headless: false, mtimeMs: 1 },
  { id: 'B', title: 'B', note: null, cwd: null, headless: false, mtimeMs: 2 },
  { id: 'C', title: 'C', note: null, cwd: null, headless: false, mtimeMs: 3 },
];

const msgView = (id: string, text: string) => ({ id, role: 'assistant', text, tsMs: 1 });

beforeEach(() => {
  setActivePinia(createPinia());
  vi.restoreAllMocks();
});

describe('A: 游标只增不减', () => {
  it('无 seq 快照不回退 lastSeq', () => {
    const messages = useMessagesStore();
    messages.setLastSeq(50);
    messages.applySnapshot({ messages: [msgView('m1', 'B 历史')] });
    expect(messages.items.map((m) => m.content)).toEqual(['B 历史']);
    expect(messages.lastSeq).toBe(50);
  });

  it('有 seq 快照可推进但不回退', () => {
    const messages = useMessagesStore();
    messages.setLastSeq(50);
    messages.applySnapshot({ seq: 60, messages: [] });
    expect(messages.lastSeq).toBe(60);
    messages.applySnapshot({ seq: 10, messages: [] });
    expect(messages.lastSeq).toBe(60);
  });

  it('clear 只清条目不碰游标，resetCursor 才清零', () => {
    const messages = useMessagesStore();
    messages.setLastSeq(50);
    messages.applySnapshot({ messages: [msgView('m1', 'x')] });
    messages.clear();
    expect(messages.items).toEqual([]);
    expect(messages.lastSeq).toBe(50);
    messages.resetCursor();
    expect(messages.lastSeq).toBe(0);
  });
});

describe('B: select 切换链', () => {
  it('快速连点只保留最后一次，无旧快照覆盖', async () => {
    const sessions = useSessionsStore();
    const messages = useMessagesStore();
    sessions.items = listFixture() as never;
    sessions.currentId = 'A';
    messages.setLastSeq(50);
    messages.applySnapshot({ messages: [msgView('mA', 'A 历史')] });

    const resumeB = deferred<{ count: number; messages: unknown[] }>();
    const resumeC = deferred<{ count: number; messages: unknown[] }>();
    const snapOf = (id: string) => ({ session: { id, cwd: '(mock)', historyPath: `(mock)/${id}` } });
    vi.spyOn(transport, 'request').mockImplementation(async (method: string, params: unknown) => {
      if (method === 'session.resume') {
        const id = (params as { id: string }).id;
        if (id === 'B') return resumeB.promise as never;
        if (id === 'C') return resumeC.promise as never;
        throw new Error('unexpected');
      }
      if (method === 'state.snapshot') {
        return snapOf(sessions.currentId || 'C') as never;
      }
      throw new Error(`unexpected ${method}`);
    });

    const pB = sessions.select('B');
    const pC = sessions.select('C');
    // C 先回来，B 后回来（过期）。
    resumeC.resolve({ count: 1, messages: [msgView('mC', 'C 历史')] });
    await pC;
    expect(sessions.currentId).toBe('C');
    expect(messages.items.map((m) => m.content)).toEqual(['C 历史']);
    resumeB.resolve({ count: 1, messages: [msgView('mB', 'B 历史')] });
    await pB;
    // B 已过期：不覆盖 C。
    expect(sessions.currentId).toBe('C');
    expect(messages.items.map((m) => m.content)).toEqual(['C 历史']);
    expect(messages.lastSeq).toBe(50);
    expect(messages.loading).toBe(false);
  });

  it('busy 中切换不切 currentId、不清空当前页', async () => {
    const sessions = useSessionsStore();
    const messages = useMessagesStore();
    sessions.items = listFixture() as never;
    sessions.currentId = 'A';
    messages.applySnapshot({ messages: [msgView('mA', 'A 历史')] });
    vi.spyOn(transport, 'request').mockRejectedValue(Object.assign(new Error('state.busy'), { code: 'state.busy' }));
    await expect(sessions.select('B')).rejects.toThrow();
    expect(sessions.currentId).toBe('A');
    expect(messages.items.map((m) => m.content)).toEqual(['A 历史']);
  });

  it('缺 messages 字段回退 loadSnapshot', async () => {
    const sessions = useSessionsStore();
    const messages = useMessagesStore();
    sessions.items = listFixture() as never;
    sessions.currentId = 'A';
    vi.spyOn(transport, 'request').mockImplementation(async (method: string) => {
      if (method === 'session.resume') return { count: 0 } as never;
      if (method === 'state.snapshot') {
        return { session: { id: 'B', cwd: '(mock)', historyPath: 'x' }, messages: [msgView('mB', '快照历史')] } as never;
      }
      throw new Error(`unexpected ${method}`);
    });
    await sessions.select('B');
    expect(sessions.currentId).toBe('B');
    expect(messages.items.map((m) => m.content)).toEqual(['快照历史']);
  });
});

describe('C: 切换中过期帧丢弃', () => {
  it('切换中 contextCleared 被丢弃但游标推进', async () => {
    const sessions = useSessionsStore();
    const messages = useMessagesStore();
    sessions.items = listFixture() as never;
    sessions.currentId = 'A';
    messages.setLastSeq(50);
    messages.applySnapshot({ messages: [msgView('mA', 'A 历史')] });

    const resumeB = deferred<{ count: number; messages: unknown[] }>();
    vi.spyOn(transport, 'request').mockImplementation(async (method: string) => {
      if (method === 'session.resume') return resumeB.promise as never;
      if (method === 'state.snapshot') return { session: { id: 'B' } } as never;
      throw new Error(`unexpected ${method}`);
    });
    const p = sessions.select('B');
    await Promise.resolve();
    // 切换中：旧会话 contextCleared 到达 → 丢弃，游标仍推进。
    dispatchFrames([{ t: 'evt', seq: 51, k: 'session.contextCleared', p: {} }]);
    expect(messages.items.map((m) => m.content)).toEqual(['A 历史']);
    expect(messages.lastSeq).toBe(51);
    resumeB.resolve({ count: 1, messages: [msgView('mB', 'B 历史')] });
    await p;
    expect(messages.items.map((m) => m.content)).toEqual(['B 历史']);
  });
});

describe('D: Mock 多会话历史', () => {
  it('list 有 2 个会话且 resume 各有消息', () => {
    const list = BEHAVIORS['session.list'];
    expect(list?.kind).toBe('ok');
    if (list?.kind !== 'ok') return;
    const res = list.run(null) as { items: Array<{ id: string }> };
    expect(res.items.length).toBeGreaterThanOrEqual(2);
    const resume = BEHAVIORS['session.resume'];
    expect(resume?.kind).toBe('ok');
    if (resume?.kind !== 'ok') return;
    for (const item of res.items.slice(0, 2)) {
      const r = resume.run({ id: item.id }) as { count: number; messages: unknown[] };
      expect(r.count).toBeGreaterThan(0);
      expect(r.messages.length).toBeGreaterThan(0);
    }
  });

  it('prompt 落盘进当前会话，切走切回仍在', () => {
    const prompt = BEHAVIORS['agent.prompt'];
    const resume = BEHAVIORS['session.resume'];
    expect(prompt?.kind).toBe('ok');
    expect(resume?.kind).toBe('ok');
    if (prompt?.kind !== 'ok' || resume?.kind !== 'ok') return;
    (resume.run as (p: unknown) => unknown)({ id: 's-mock-1' });
    const before = (resume.run({ id: 's-mock-1' }) as { messages: unknown[] }).messages.length;
    prompt.run({ text: 'mock 回归探针' });
    const after = (resume.run({ id: 's-mock-1' }) as { messages: unknown[] }).messages.length;
    expect(after).toBe(before + 1);
  });
});
