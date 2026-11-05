// useSessionUsage（本会话合计拉取语义）。
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { nextTick } from 'vue';
import { useSessionStore } from '@/stores/session';
import { useSessionUsage } from './useSessionUsage';
import { transport } from '@/transport';

function deferred<T>() {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

beforeEach(() => {
  setActivePinia(createPinia());
  vi.restoreAllMocks();
});

describe('useSessionUsage', () => {
  it('session.id 为空不发请求，直接零态', async () => {
    const spy = vi.spyOn(transport, 'request').mockResolvedValue({} as never);
    const u = useSessionUsage();
    await u.refresh();
    expect(spy).not.toHaveBeenCalled();
    expect(u.summary.value).toBeNull();
    expect(u.failed.value).toBe(false);
  });

  it('传参名冻结为 {sessionId}', async () => {
    const session = useSessionStore();
    session.applyInfo({ sessionId: 's-9' });
    const spy = vi.spyOn(transport, 'request').mockResolvedValue({
      input: 10,
      cached: 2,
      output: 5,
      total: 15,
      requests: 1,
      llmCalls: 5,
      hitRate: 0.2,
      byDay: [],
      byModel: [],
    } as never);
    const u = useSessionUsage();
    await u.refresh();
    expect(spy).toHaveBeenCalledWith('usage.summary', { sessionId: 's-9' });
    expect(u.summary.value?.input).toBe(10);
    expect(u.summary.value?.llmCalls).toBe(5); // llmCalls 透传
  });

  it('切会话重查并丢弃旧响应（queryId）', async () => {
    const session = useSessionStore();
    session.applyInfo({ sessionId: 's-A' });
    const gateA = deferred<unknown>();
    const spy = vi.spyOn(transport, 'request').mockImplementation(((
      _method: string,
      p: unknown
    ) => {
      const sid = (p as { sessionId?: string }).sessionId;
      if (sid === 's-A') return gateA.promise as never;
      return Promise.resolve({
        input: 7,
        cached: 0,
        output: 1,
        total: 8,
        requests: 1,
        hitRate: 0,
        byDay: [],
        byModel: [],
      }) as never;
    }) as never);
    const u = useSessionUsage();
    const first = u.refresh();
    // 切会话：旧响应必须丢弃
    session.id = 's-B';
    await nextTick();
    gateA.resolve({
      input: 100,
      cached: 0,
      output: 100,
      total: 200,
      requests: 9,
      hitRate: 0,
      byDay: [],
      byModel: [],
    });
    await first;
    // 等待 s-B 的请求完成
    await vi.waitFor(() => expect(u.summary.value?.input).toBe(7));
    expect(spy).toHaveBeenCalledWith('usage.summary', { sessionId: 's-B' });
  });

  it('失败置 failed，重试可恢复', async () => {
    const session = useSessionStore();
    session.applyInfo({ sessionId: 's-F' });
    const spy = vi.spyOn(transport, 'request').mockRejectedValueOnce(new Error('db down'));
    const u = useSessionUsage();
    await u.refresh();
    expect(u.failed.value).toBe(true);
    spy.mockResolvedValue({
      input: 1,
      cached: 0,
      output: 1,
      total: 2,
      requests: 1,
      hitRate: 0,
      byDay: [],
      byModel: [],
    } as never);
    await u.refresh();
    expect(u.failed.value).toBe(false);
    expect(u.summary.value?.total).toBe(2);
  });
});
