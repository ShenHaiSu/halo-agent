/** 目录桥方法名契约：CefSharp DefaultBinder 以 camelCase 暴露方法，
 *  这里用真实 createHostBridgeApi + 假 transport 断言 t.call 收到的方法名，
 *  防止 PascalCase 方法名回归（此前的盲区：store 单测直接 mock getHostBridge 绕过了 transport）。 */
import { describe, expect, it, vi } from 'vitest';
import { createHostBridgeApi } from './index';
import type { BridgeTransport } from '../transport/transport';

function recordingTransport(reply: unknown): BridgeTransport {
  const call = vi.fn(async () => reply);
  return {
    call: call as unknown as BridgeTransport['call'],
    supports: () => true,
    version: async () => 2,
  };
}

const emptyFetch = {
  ok: true,
  data: {
    providerId: 'p',
    modelsUrl: 'https://api.openai.com/v1/models',
    effectiveProxySummary: 'DIRECT',
    elapsedMs: 1,
    models: [],
  },
};

describe('modelCatalog 桥方法名（camelCase 契约）', () => {
  it('fetchProviderModels 调用 camelCase 方法名', async () => {
    const t = recordingTransport(emptyFetch);
    await createHostBridgeApi(t).fetchProviderModels('p');
    expect(t.call).toHaveBeenCalledWith('fetchProviderModels', 'p');
    const calledMethod = (t.call as ReturnType<typeof vi.fn>).mock.calls[0][0];
    expect(calledMethod).toBe(calledMethod[0].toLowerCase() + calledMethod.slice(1));
  });

  it('importDiscoveredModels 调用 camelCase 方法名', async () => {
    const t = recordingTransport({ ok: true, data: { providers: [] } });
    await createHostBridgeApi(t).importDiscoveredModels({ providerId: 'p', modelIds: ['m'], importDisabled: false });
    const calledMethod = (t.call as ReturnType<typeof vi.fn>).mock.calls[0][0];
    expect(calledMethod).toBe('importDiscoveredModels');
  });

  it('setDiscoveredModelsIgnored 调用 camelCase 方法名', async () => {
    const t = recordingTransport({ ok: true, data: true });
    await createHostBridgeApi(t).setDiscoveredModelsIgnored('p', ['m'], true);
    const calledMethod = (t.call as ReturnType<typeof vi.fn>).mock.calls[0][0];
    expect(calledMethod).toBe('setDiscoveredModelsIgnored');
  });
});
