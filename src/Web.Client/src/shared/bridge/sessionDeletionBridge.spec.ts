import { describe, expect, it, vi } from 'vitest';
import { createHostBridgeApi } from './api';
import type { SessionDeletionResultDto } from './protocol/types';
import { BRIDGE_VERSION } from './protocol/version';
import type { BridgeTransport } from './transport/transport';

function makeTransport(supported: boolean, result?: SessionDeletionResultDto) {
  const call = vi.fn(async (..._args: unknown[]) => ({ ok: true, data: result }));
  const transport: BridgeTransport = {
    call: call as unknown as BridgeTransport['call'],
    supports: (method: string) => supported && method === 'deleteSessionV2',
    version: async () => BRIDGE_VERSION,
  };
  return { transport, call };
}

describe('安全会话删除桥契约', () => {
  it('V2 透传严格结果且不回退旧 bool 入口', async () => {
    const result: SessionDeletionResultDto = {
      outcome: 'partial',
      errorCode: 'SESSION_DELETION_PARTIAL',
      destructiveCommitCompleted: true,
      backupFileName: 'history.db.bak',
      deletedSessionIds: ['sess-root', 'sess-child'],
      residualSessionIds: ['sess-child'],
      database: 'completed',
      directories: 'failed',
      index: 'completed',
      warnings: ['DIRECTORY_DELETE_FAILED'],
    };
    const { transport, call } = makeTransport(true, result);
    const api = createHostBridgeApi(transport);

    const response = await api.deleteSessionV2('ws-delete', 'sess-root');

    expect(response.ok).toBe(true);
    expect(response.data).toEqual(result);
    expect(call).toHaveBeenCalledWith('deleteSessionV2', 'ws-delete', 'sess-root');
    expect(call).toHaveBeenCalledTimes(1);
  });

  it('旧宿主缺少 V2 时明确返回不支持且不调用删除', async () => {
    const { transport, call } = makeTransport(false);
    const api = createHostBridgeApi(transport);

    const response = await api.deleteSessionV2('ws-delete', 'sess-root');

    expect(response).toMatchObject({ ok: false, code: 'SESSION_DELETE_UNSUPPORTED' });
    expect(call).not.toHaveBeenCalled();
  });
});
