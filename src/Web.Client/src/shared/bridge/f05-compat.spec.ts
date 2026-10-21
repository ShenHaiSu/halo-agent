/** need21-F05: compat 7→6→5 回退 + BRIDGE_VERSION 单测（mock Transport/raw 函数）。 */
import { describe, expect, it, vi } from 'vitest';
import { compatCallStartAgentTurn, type RawStartAgentTurn } from '@shared/bridge/compat';
import { assertBridgeVersion, BRIDGE_VERSION } from '@shared/bridge/protocol/version';
import { CefTransport } from '@shared/bridge/transport/cef';
import { MockTransport } from '@shared/bridge/transport/mock';
import type { HostBridgeApi } from '@shared/bridge/protocol/types';

const params = { workspaceId: 'w', sessionId: 's', userText: 'hi', providerId: 'p', modelId: 'm', attachmentsJson: '[]', requestId: 'r1' };

describe('compatCallStartAgentTurn 7→6→5 回退', () => {
  it('新后端 7 参一次成功', async () => {
    const raw: RawStartAgentTurn = vi.fn(async (...args: string[]) => {
      expect(args).toHaveLength(7);
      return { ok: true };
    });
    await expect(compatCallStartAgentTurn(raw, params)).resolves.toEqual({ ok: true });
    expect(raw).toHaveBeenCalledTimes(1);
  });
  it('老后端 7 参失败→6 参成功（6 参不带 requestId）', async () => {
    const calls: string[][] = [];
    const raw: RawStartAgentTurn = async (...args: string[]) => {
      calls.push(args);
      if (args.length === 7) throw new Error('no 7-arg');
      return { ok: true };
    };
    await expect(compatCallStartAgentTurn(raw, params)).resolves.toEqual({ ok: true });
    expect(calls.map((c) => c.length)).toEqual([7, 6]);
  });
  it('更老后端 7/6 失败→5 参成功', async () => {
    const calls: string[][] = [];
    const raw: RawStartAgentTurn = async (...args: string[]) => {
      calls.push(args);
      if (args.length > 5) throw new Error(`no ${args.length}-arg`);
      return { ok: true };
    };
    await expect(compatCallStartAgentTurn(raw, params)).resolves.toEqual({ ok: true });
    expect(calls.map((c) => c.length)).toEqual([7, 6, 5]);
  });
  it('全失败抛 AGENT_START_ERROR', async () => {
    const raw: RawStartAgentTurn = async () => {
      throw new Error('down');
    };
    await expect(compatCallStartAgentTurn(raw, params)).rejects.toMatchObject({ code: 'AGENT_START_ERROR' });
  });
});

describe('BRIDGE_VERSION', () => {
  it('一致通过', () => {
    expect(assertBridgeVersion(BRIDGE_VERSION)).toBe(BRIDGE_VERSION);
  });
  it('缺失按 legacy(0) 继续', () => {
    expect(assertBridgeVersion(null)).toBe(0);
    expect(assertBridgeVersion(undefined)).toBe(0);
  });
  it('不一致抛 BRIDGE_VERSION_MISMATCH', () => {
    expect(() => assertBridgeVersion(BRIDGE_VERSION + 99)).toThrow();
  });
});

describe('Transport（mock Cef 原生对象）', () => {
  it('CefTransport 缺方法抛 BRIDGE_NOT_CONNECTED', async () => {
    const t = new CefTransport({});
    await expect(t.call('startAgentTurn', 'a')).rejects.toMatchObject({ code: 'BRIDGE_NOT_CONNECTED' });
    expect(t.supports('startAgentTurn')).toBe(false);
  });
  it('CefTransport version 无接口返回 null（legacy）', async () => {
    expect(await new CefTransport({}).version()).toBe(null);
  });
  it('MockTransport version 与前端 BRIDGE_VERSION 一致', async () => {
    const fakeApi = { startAgentTurn: async () => ({ ok: true }) } as unknown as HostBridgeApi;
    const mt = new MockTransport(fakeApi);
    expect(await mt.version()).toBe(BRIDGE_VERSION);
  });
});
