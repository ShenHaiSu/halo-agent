import { describe, expect, it, vi } from 'vitest';
import { createHostBridgeApi } from './api';
import type { WorkspaceRemovalResultDto } from './protocol/types';
import { BRIDGE_VERSION } from './protocol/version';
import type { BridgeTransport } from './transport/transport';

function makeTransport(supported: boolean, result?: WorkspaceRemovalResultDto) {
  const call = vi.fn(async (..._args: unknown[]) => ({ ok: true, data: result }));
  const transport: BridgeTransport = {
    call: call as unknown as BridgeTransport['call'],
    supports: (method: string) => supported && method === 'removeWorkspaceV2',
    version: async () => BRIDGE_VERSION,
  };
  return { transport, call };
}

const completed: WorkspaceRemovalResultDto = {
  outcome: 'completedWithWarnings',
  mode: 'purge',
  errorCode: null,
  workspaceUnbound: true,
  sessionData: 'deleted',
  haloAgent: 'failed',
  haloAgentReason: 'DELETE_FAILED',
  failedPaths: ['C:\\project\\.haloagent'],
  warnings: ['HALO_AGENT_DELETE_FAILED'],
};

describe('工作区双模式桥契约', () => {
  it('V2 透传 mode 与严格分阶段结果，不回退旧 bool 入口', async () => {
    const { transport, call } = makeTransport(true, completed);
    const api = createHostBridgeApi(transport);

    const response = await api.removeWorkspaceV2('ws-removal', 'purge');

    expect(response.ok).toBe(true);
    expect(response.data).toEqual(completed);
    expect(call).toHaveBeenCalledWith('removeWorkspaceV2', 'ws-removal', 'purge');
    expect(call).toHaveBeenCalledTimes(1);
  });

  it('旧宿主缺少 V2 时明确返回不支持且不发起删除', async () => {
    const { transport, call } = makeTransport(false);
    const api = createHostBridgeApi(transport);

    const response = await api.removeWorkspaceV2('ws-removal', 'purge');

    expect(response).toMatchObject({ ok: false, code: 'WORKSPACE_REMOVAL_UNSUPPORTED' });
    expect(call).not.toHaveBeenCalled();
  });
});
