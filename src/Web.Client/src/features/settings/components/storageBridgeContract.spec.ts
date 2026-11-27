import { describe, expect, it, vi } from 'vitest';
import { createSystemApi } from '@shared/bridge/api/system';
import { MockTransport } from '@shared/bridge/transport/mock';
import { mockSystemMethods } from '@/mocks/mockSystem';
import type { BridgeTransport } from '@shared/bridge/transport/transport';
import type { HostBridgeApi } from '@shared/bridge/protocol/hostApi';

function fakeTransport(call: ReturnType<typeof vi.fn>, supported = true): BridgeTransport {
  return {
    call,
    supports: vi.fn().mockReturnValue(supported),
    version: vi.fn().mockResolvedValue(1),
  };
}

describe('存储增强桥契约一致性', () => {
  it('新方法映射到正确的桥方法名并原样透传参数', async () => {
    const call = vi.fn().mockResolvedValue({ ok: true, data: 7 });
    const api = createSystemApi(fakeTransport(call));

    const retention = await api.setLogRetentionDays(7);
    expect(call).toHaveBeenLastCalledWith('setLogRetentionDays', 7);
    expect(retention.ok).toBe(true);

    call.mockResolvedValue({ ok: true, data: '{}' });
    await api.cleanupImageAttachments(10, false);
    expect(call).toHaveBeenLastCalledWith('cleanupImageAttachments', 10, false);

    call.mockResolvedValue({ ok: true, data: { rootPath: 'D:\\Data', totalBytes: 0, items: [] } });
    await api.getStorageBreakdown();
    expect(call).toHaveBeenLastCalledWith('getStorageBreakdown');

    call.mockResolvedValue({ ok: true, data: { success: true, beforeBytes: 10, afterBytes: 8, reclaimedBytes: 2, walCheckpointed: true } });
    await api.compactWorkspaceHistory('ws_a');
    expect(call).toHaveBeenLastCalledWith('compactWorkspaceHistory', 'ws_a');

    call.mockResolvedValue({ ok: true, data: { success: true, freedBytes: 4, backupExisted: true } });
    await api.deleteWorkspaceBackup('ws_a');
    expect(call).toHaveBeenLastCalledWith('deleteWorkspaceBackup', 'ws_a');

    call.mockResolvedValue({ ok: true, data: [] });
    await api.getRecentHostEvents(5);
    expect(call).toHaveBeenLastCalledWith('getRecentHostEvents', 5);
  });

  it('后端不支持新方法时返回 ok=false 而不抛', async () => {
    const call = vi.fn();
    const api = createSystemApi(fakeTransport(call, false));

    const retention = await api.setLogRetentionDays(7);
    expect(retention.ok).toBe(false);
    expect(retention.code).toBe('STORAGE_UNSUPPORTED');
    expect(call).not.toHaveBeenCalled();
  });

  it('mock 实现了全部新方法，且经 transport 调用签名一致', async () => {
    const mockApi = mockSystemMethods({ mockDataRoot: 'D:\\Data' } as never);
    expect(typeof mockApi.setLogRetentionDays).toBe('function');
    expect(typeof mockApi.getStorageBreakdown).toBe('function');
    expect(typeof mockApi.compactWorkspaceHistory).toBe('function');
    expect(typeof mockApi.deleteWorkspaceBackup).toBe('function');
    expect(typeof mockApi.getRecentHostEvents).toBe('function');
    expect(typeof mockApi.cleanupImageAttachments).toBe('function');

    const api = createSystemApi(new MockTransport(mockApi as HostBridgeApi));
    const cleanup = await api.cleanupImageAttachments(7, false);
    expect(cleanup.ok).toBe(true);
    const report = JSON.parse(cleanup.data ?? '{}');
    expect(report.expiredFiles).toBe(3);
    expect(report.dryRun).toBe(false);

    const breakdown = await api.getStorageBreakdown();
    expect(breakdown.ok).toBe(true);
    expect(breakdown.data?.totalBytes).toBe(breakdown.data?.items.reduce((sum, item) => sum + item.sizeBytes, 0));

    const compact = await api.compactWorkspaceHistory('ws_mock');
    expect(compact.ok).toBe(true);
    expect(compact.data?.success).toBe(true);

    const backupCleanup = await api.deleteWorkspaceBackup('ws_mock');
    expect(backupCleanup.ok).toBe(true);
    expect(backupCleanup.data?.success).toBe(true);
  });
});
