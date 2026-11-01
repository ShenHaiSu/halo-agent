// 启动门（boot gate）回归：等引擎就绪再放行首屏请求，超时降级不卡死。
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { useBootStore } from './boot';
import { transport } from '@/transport';

beforeEach(() => {
  setActivePinia(createPinia());
  vi.restoreAllMocks();
  // node 单测环境下 isMock 本来就为 false（location 不可用→回退 false），这里显式锁死意图。
  vi.spyOn(transport, 'isMock', 'get').mockReturnValue(false);
});

describe('boot 启动门', () => {
  it('引擎已就绪时放行并置 ready', async () => {
    vi.spyOn(transport, 'isReady').mockResolvedValue(true);
    const boot = useBootStore();
    await expect(boot.waitForEngine({ minSplashMs: 0 })).resolves.toBe('ready');
    expect(boot.phase).toBe('ready');
    expect(boot.isBooting).toBe(false);
    expect(boot.ready).toBe(true);
  });

  it('引擎稍后就绪则等待后放行', async () => {
    const spy = vi
      .spyOn(transport, 'isReady')
      .mockResolvedValueOnce(false)
      .mockResolvedValueOnce(false)
      .mockResolvedValue(true);
    const boot = useBootStore();
    await expect(boot.waitForEngine({ pollMs: 5, minSplashMs: 0 })).resolves.toBe('ready');
    expect(spy.mock.calls.length).toBeGreaterThanOrEqual(3);
    expect(boot.phase).toBe('ready');
  });

  it('isReady 抛错也视为未就绪并继续轮询', async () => {
    const spy = vi
      .spyOn(transport, 'isReady')
      .mockRejectedValueOnce(new Error('引擎桥接不可用'))
      .mockResolvedValue(true);
    const boot = useBootStore();
    await expect(boot.waitForEngine({ pollMs: 5, minSplashMs: 0 })).resolves.toBe('ready');
    expect(spy.mock.calls.length).toBeGreaterThanOrEqual(2);
  });

  it('超时则降级放行，不再阻塞首屏请求', async () => {
    vi.spyOn(transport, 'isReady').mockResolvedValue(false);
    const boot = useBootStore();
    await expect(boot.waitForEngine({ pollMs: 5, timeoutMs: 30, minSplashMs: 0 })).resolves.toBe(
      'degraded'
    );
    expect(boot.ready).toBe(true);
    expect(boot.isBooting).toBe(false);
  });

  it('mock 环境直接放行，不轮询引擎', async () => {
    vi.spyOn(transport, 'isMock', 'get').mockReturnValue(true);
    const spy = vi.spyOn(transport, 'isReady');
    const boot = useBootStore();
    await expect(boot.waitForEngine({ minSplashMs: 0 })).resolves.toBe('ready');
    expect(spy).not.toHaveBeenCalled();
  });

  it('重复调用复用同一 Promise，不重复轮询', async () => {
    const spy = vi.spyOn(transport, 'isReady').mockResolvedValue(true);
    const boot = useBootStore();
    const [a, b] = await Promise.all([
      boot.waitForEngine({ minSplashMs: 0 }),
      boot.waitForEngine({ minSplashMs: 0 }),
    ]);
    expect(a).toBe('ready');
    expect(b).toBe('ready');
    expect(spy.mock.calls.length).toBe(1);
  });
});
