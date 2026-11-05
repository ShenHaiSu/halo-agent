// 回归：切换窗口内 turn.finished 不再被丢弃；外来 turn 不抢 currentTurn。
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { useMessagesStore } from './messages';
import { useSessionsStore } from './sessions';
import { useStreamStore } from './stream';
import { dispatchFrames } from './eventBridge';
import { transport } from '@/transport';

function deferred<T>() {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

const listFixture = () => [
  { id: 'A', title: 'A', note: null, cwd: null, headless: false, mtimeMs: 1 },
  { id: 'B', title: 'B', note: null, cwd: null, headless: false, mtimeMs: 2 },
];

beforeEach(() => {
  setActivePinia(createPinia());
  vi.restoreAllMocks();
});

describe('switching 中 turn.finished 正常收口', () => {
  it('切换中本轮 turn.finished 到达 → busy=false、currentTurn=null、turn 已 finalize', async () => {
    const sessions = useSessionsStore();
    const messages = useMessagesStore();
    const stream = useStreamStore();
    sessions.items = listFixture() as never;
    sessions.currentId = 'A';
    messages.setLastSeq(50);

    const resumeB = deferred<{ count: number; messages: unknown[] }>();
    vi.spyOn(transport, 'request').mockImplementation(async (method: string) => {
      if (method === 'session.resume') return resumeB.promise as never;
      if (method === 'state.snapshot') return { session: { id: 'B' } } as never;
      throw new Error(`unexpected ${method}`);
    });
    // 空闲时发起切换（预检通过，进入 switching 窗口），随后才开始一轮并收口
    const p = sessions.select('B');
    await Promise.resolve();
    stream.beginTurn('t1');
    messages.ensureTurn('t1');
    // 切换窗口内本轮收口帧到达：不再被丢弃
    dispatchFrames([{ t: 'evt', seq: 51, k: 'turn.finished', p: { turnId: 't1', finalUsed: 1, text: 'done' } } as never]);
    expect(stream.busy).toBe(false);
    expect(stream.currentTurn).toBeNull();
    expect(messages.findTurn('t1')?.final.streaming).toBe(false);
    expect(messages.findTurn('t1')?.final.content).toBe('done');
    resumeB.resolve({ count: 1, messages: [] });
    await p;
  });

  it('切换中外来 turn.finished 不抢 currentTurn、不调 endTurn', async () => {
    const sessions = useSessionsStore();
    const messages = useMessagesStore();
    const stream = useStreamStore();
    sessions.items = listFixture() as never;
    sessions.currentId = 'A';
    messages.setLastSeq(50);

    const resumeB = deferred<{ count: number; messages: unknown[] }>();
    vi.spyOn(transport, 'request').mockImplementation(async (method: string) => {
      if (method === 'session.resume') return resumeB.promise as never;
      if (method === 'state.snapshot') return { session: { id: 'B' } } as never;
      throw new Error(`unexpected ${method}`);
    });
    // 空闲切换在途，随后当前轮与旧会话迟到轮同时存在
    const p = sessions.select('B');
    await Promise.resolve();
    stream.beginTurn('t-current');
    messages.ensureTurn('t-current');
    messages.ensureTurn('t-old');
    dispatchFrames([{ t: 'evt', seq: 51, k: 'turn.finished', p: { turnId: 't-old', finalUsed: 1, text: 'old' } } as never]);
    // 外来收口只落盘，不抢占当前轮
    expect(stream.currentTurn?.turnId).toBe('t-current');
    expect(stream.busy).toBe(true);
    expect(messages.findTurn('t-old')?.final.content).toBe('old');
    resumeB.resolve({ count: 1, messages: [] });
    await p;
  });
});
