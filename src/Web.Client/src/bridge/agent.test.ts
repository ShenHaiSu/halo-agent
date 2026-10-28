// src/Web.Client/src/bridge/agent.test.ts
// 桥 JSON 字符串契约回归（dist 骨架屏根因）：
// C# 桥（AgentBridge/HaloHostBridge）返回的一律是 JSON 字符串，前端负责 parse。
// 若 C# 又直接返回 DTO/JsonElement（PascalCase/代理），这里会失败。
import { describe, it, expect, beforeEach } from 'vitest';
import { __resetBridgeForTest, bindBridge } from './bind';
import { agentBridge } from './agent';
import { hostBridge } from './host';

function stubCefHost(opts: { request?: unknown; pollEvents?: unknown; systemInfo?: unknown }): void {
  const w = window as unknown as Record<string, unknown>;
  w.CefSharp = { BindObjectAsync: async () => true };
  w.haloAgent = {
    request: async () => opts.request,
    pollEvents: async () => opts.pollEvents,
    isReady: async () => true,
  };
  w.haloHost = {
    systemInfo: async () => opts.systemInfo,
    windowState: async () =>
      '{"maximized":false,"minimized":false,"width":1,"height":1,"dpi":96,"titleBarHeight":40}',
    windowMinimize: async () => undefined,
    windowMaximizeOrRestore: async () => '{}',
    windowClose: async () => undefined,
    openExternal: async () => undefined,
    showItemInFolder: async () => undefined,
    copyText: async () => undefined,
    readClipboardImage: async () => null,
    toggleDevTools: async () => undefined,
    restartEngine: async () => undefined,
  };
}

beforeEach(() => {
  __resetBridgeForTest();
  const w = window as unknown as Record<string, unknown>;
  delete w.haloAgent;
  delete w.haloHost;
  delete w.CefSharp;
  window.localStorage.clear();
});

describe('桥 JSON 字符串契约（dist 骨架屏回归）', () => {
  it('request 解析 C# 返回的 JSON 字符串', async () => {
    stubCefHost({ request: '{"items":[]}', pollEvents: '{"events":[],"nextSeq":3}' });
    await bindBridge();
    await expect(agentBridge.request('session.list', null)).resolves.toEqual({ items: [] });
  });

  it('pollEvents 解析事件批 JSON（含 t/seq/k/p 单字母帧）', async () => {
    stubCefHost({
      request: 'null',
      pollEvents: '{"events":[{"t":"evt","seq":4,"k":"notice","p":{}}],"nextSeq":4}',
    });
    await bindBridge();
    const r = await agentBridge.pollEvents(3);
    expect(r.nextSeq).toBe(4);
    expect(r.events).toHaveLength(1);
    expect(r.events[0]).toMatchObject({ t: 'evt', seq: 4, k: 'notice' });
  });

  it('pollEvents 遇到 PascalCase 旧形状时给安全默认（不吞错、不断链）', async () => {
    stubCefHost({ request: 'null', pollEvents: '{"Events":[],"NextSeq":9}' });
    await bindBridge();
    const r = await agentBridge.pollEvents(3);
    expect(r).toEqual({ events: [], nextSeq: 3 });
  });

  it('hostBridge.systemInfo 解析 C# 返回的 JSON 字符串', async () => {
    stubCefHost({ systemInfo: '{"osDescription":"Win","isWindows7":false,"isFirstRun":true}' });
    await bindBridge();
    const si = await hostBridge.systemInfo();
    expect(si.osDescription).toBe('Win');
    expect(si.isWindows7).toBe(false);
  });
});
