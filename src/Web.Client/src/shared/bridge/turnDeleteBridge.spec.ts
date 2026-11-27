import { describe, expect, it, vi } from 'vitest';
import { createHostBridgeApi } from './api';
import { CefTransport } from './transport/cef';

describe('整轮删除桥接入口', () => {
  it('旧宿主缺少方法时返回稳定能力错误，不发桥调用', async () => {
    const api = createHostBridgeApi(new CefTransport({}));
    const response = await api.deleteSessionTurn('ws-a', 'sess-a', 'msg-a');
    expect(response.ok).toBe(false);
    expect(response.code).toBe('TURN_DELETE_UNSUPPORTED');
  });

  it('新宿主按方法名直透三参数并保持 camelCase 结果', async () => {
    const call = vi.fn().mockResolvedValue({
      ok: true,
      data: {
        success: true,
        deletedTurnId: 'uturn-msg-a',
        deletedMessages: 2,
        keptMessages: 1,
        syntheticAppended: 0,
        orphanToolsRemoved: 0,
        destructiveCommitCompleted: true,
      },
    });
    const api = createHostBridgeApi(new CefTransport({ deleteSessionTurn: call }));
    const response = await api.deleteSessionTurn('ws-a', 'sess-a', 'msg-a');
    expect(call).toHaveBeenCalledWith('ws-a', 'sess-a', 'msg-a');
    expect(response.ok).toBe(true);
    expect(response.data?.deletedTurnId).toBe('uturn-msg-a');
    expect(response.data?.destructiveCommitCompleted).toBe(true);
  });
});
