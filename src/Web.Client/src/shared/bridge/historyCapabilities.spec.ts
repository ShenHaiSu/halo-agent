import { describe, expect, it, vi } from 'vitest';
import { UnsupportedHistoryCapabilities, normalizeHistoryCapabilities } from './protocol/domain';
import { BRIDGE_VERSION, assertBridgeVersion, assertBridgeVersionAgainst } from './protocol/version';
import { createHostBridgeApi } from './api';
import { CefTransport } from './transport/cef';

const full = {
  historyAnchors: true,
  replayImages: true,
  atomicHistoryMutex: true,
  strictBranchRollback: true,
};

describe('历史能力位与协议版本契约', () => {
  it('桥协议保持版本 2，新旧后端可握手', () => {
    expect(BRIDGE_VERSION).toBe(2);
    expect(assertBridgeVersion(2)).toBe(2);
  });

  it('相邻版本反例：期望 3 后端 2 时必须阻断', () => {
    expect(() => assertBridgeVersionAgainst(2, 3)).toThrow(/桥接版本不一致/);
  });

  it('能力字段缺失或类型错误归一为不支持', () => {
    expect(normalizeHistoryCapabilities(undefined)).toEqual(UnsupportedHistoryCapabilities);
    expect(normalizeHistoryCapabilities({ historyAnchors: 'yes' })).toEqual(UnsupportedHistoryCapabilities);
    expect(normalizeHistoryCapabilities({ historyAnchors: true })).toEqual({
      ...UnsupportedHistoryCapabilities,
      historyAnchors: true,
    });
  });

  it('Cef 通道：旧后端无能力方法时归一为不支持且不抛错', async () => {
    const api = createHostBridgeApi(new CefTransport({ getBridgeVersion: async () => 2 }));
    const response = await api.getHistoryCapabilities('ws', 'sess');
    expect(response.ok).toBe(true);
    expect(response.data).toEqual(UnsupportedHistoryCapabilities);
  });

  it('Cef 通道：新后端能力字段全部映射', async () => {
    const getHistoryCapabilities = vi.fn().mockResolvedValue({
      ok: true,
      data: full,
    });
    const api = createHostBridgeApi(new CefTransport({ getHistoryCapabilities }));
    const response = await api.getHistoryCapabilities('ws', 'sess');
    expect(response.data).toEqual(full);
  });

  it('能力探测失败归一为不支持，不升级成应用错误', async () => {
    const getHistoryCapabilities = vi.fn().mockRejectedValue(new Error('old backend'));
    const api = createHostBridgeApi(new CefTransport({ getHistoryCapabilities }));
    const response = await api.getHistoryCapabilities('ws', 'sess');
    expect(response.ok).toBe(true);
    expect(response.data).toEqual(UnsupportedHistoryCapabilities);
  });
});
