import { describe, expect, it, vi, beforeEach } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import { createPinia, setActivePinia, type Pinia } from 'pinia';
import naive from 'naive-ui';

const { bridgeStub, messageStub } = vi.hoisted(() => ({
  bridgeStub: {
    getStorageBreakdown: vi.fn(),
    getUsageDbStatus: vi.fn(),
    cleanupImageAttachments: vi.fn(),
    cleanupExpiredLogs: vi.fn(),
    setLogRetentionDays: vi.fn(),
    validateStoragePath: vi.fn(),
    getStorageConfig: vi.fn(),
    getSystemInfo: vi.fn(),
  },
  messageStub: {
    success: vi.fn(),
    error: vi.fn(),
    info: vi.fn(),
    warning: vi.fn(),
  },
}));

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...actual, getHostBridge: () => bridgeStub };
});

vi.mock('naive-ui', async (importOriginal) => {
  const actual = await importOriginal<typeof import('naive-ui')>();
  return {
    ...actual,
    useMessage: () => messageStub,
    useDialog: () => ({
      warning: (opts: { onPositiveClick?: () => void }) => opts.onPositiveClick?.(),
      error: vi.fn(),
    }),
  };
});

import StorageSettingsPanel from './StorageSettingsPanel.vue';
import { useSystemStore } from '@/stores/systemStore';

const baseConfig = {
  dataRootPath: 'D:\\HaloAgentData',
  defaultPath: 'C:\\HaloAgentData',
  fallbackPath: 'C:\\Users\\Developer\\AppData\\Local\\HaloAgent\\Data',
  bootstrapFilePath: 'C:\\Users\\Developer\\AppData\\Local\\HaloAgent\\bootstrap.json',
  previousDataRootPath: null,
  lastModifiedUtc: '2026-11-19T10:00:00Z',
  isUsingFallback: false,
  isWritable: true,
  diskFreeSpaceMb: 51200,
  diskTotalSpaceMb: 102400,
  logRetentionDays: 14,
  logChannels: [
    {
      channel: 'app',
      label: '系统运行日志 (App)',
      fileName: 'app-20261119.log',
      filePath: 'D:\\HaloAgentData\\logs\\app-20261119.log',
      sizeBytes: 0,
      exists: true,
      lastWriteTimeUtc: null,
    },
    {
      channel: 'llm',
      label: 'LLM Trace',
      fileName: 'llm-trace-20261119.log',
      filePath: 'D:\\HaloAgentData\\logs\\llm-trace-20261119.log',
      sizeBytes: 4096,
      exists: true,
      lastWriteTimeUtc: '2026-11-19T09:00:00Z',
    },
  ],
};

const emptyBreakdown = { rootPath: 'D:\\HaloAgentData', totalBytes: 0, items: [] };

async function mountPanel(pinia: Pinia) {
  const wrapper = mount(StorageSettingsPanel, { global: { plugins: [naive, pinia] } });
  await flushPromises();
  await flushPromises();
  return wrapper;
}

describe('StorageSettingsPanel（存储与日志面板展示增强）', () => {
  let pinia: Pinia;

  beforeEach(() => {
    pinia = createPinia();
    setActivePinia(pinia);
    const store = useSystemStore();
    store.storageConfig = { ...baseConfig, logChannels: baseConfig.logChannels.map((c) => ({ ...c })) } as never;
    store.systemInfo = { logsDirectory: 'D:\\HaloAgentData\\logs' } as never;

    bridgeStub.getStorageBreakdown.mockReset().mockResolvedValue({ ok: true, data: emptyBreakdown });
    bridgeStub.getUsageDbStatus.mockReset().mockResolvedValue({
      ok: true,
      data: {
        dbPath: 'D:\\HaloAgentData\\metrics\\usage.db',
        recordCount: 42,
        dbSizeBytes: 49152,
        oldestDate: '2026-11-01',
        newestDate: '2026-11-19',
      },
    });
    bridgeStub.cleanupImageAttachments.mockReset().mockResolvedValue({
      ok: true,
      data: JSON.stringify({
        scannedFiles: 12,
        scannedBytes: 4096,
        expiredFiles: 3,
        expiredBytes: 1024,
        deletedFiles: 0,
        dryRun: true,
        retentionDays: 30,
      }),
    });
    bridgeStub.cleanupExpiredLogs.mockReset().mockResolvedValue({ ok: true, data: 0 });
    bridgeStub.setLogRetentionDays.mockReset().mockResolvedValue({ ok: true, data: 7 });
    bridgeStub.validateStoragePath.mockReset().mockResolvedValue({
      ok: true,
      data: {
        isValid: true,
        normalizedPath: 'D:\\HaloAgentData_Test',
        message: '校验通过',
        availableFreeSpaceMb: 1024,
        requiredSpaceMb: 0,
        targetNonEmpty: false,
        riskCode: null,
      },
    });
    bridgeStub.getStorageConfig.mockReset().mockResolvedValue({ ok: true, data: baseConfig });
    bridgeStub.getSystemInfo.mockReset().mockResolvedValue({ ok: true, data: { logsDirectory: 'D:\\HaloAgentData\\logs' } });
    messageStub.success.mockReset();
    messageStub.error.mockReset();
    messageStub.warning.mockReset();
  });

  it('磁盘占用率显示已用/总量，0 字节日志通道显示空文件', async () => {
    const wrapper = await mountPanel(pinia);
    const usageText = wrapper.find('[data-testid="disk-usage-text"]').text();
    expect(usageText).toContain('已用 50.0 GB / 共 100.0 GB');
    expect(wrapper.find('[data-testid="log-size-tag-app"]').text()).toContain('空文件');
    expect(wrapper.find('[data-testid="log-size-tag-llm"]').text()).not.toContain('空文件');
  });

  it('未写入通道显示“尚未写入”，已写入通道展示时间', async () => {
    const wrapper = await mountPanel(pinia);
    expect(wrapper.find('[data-testid="log-last-write-app"]').text()).toContain('尚未写入');
    expect(wrapper.find('[data-testid="log-last-write-llm"]').text()).not.toContain('尚未写入');
  });

  it('日志目录文本与配置修改时间展示', async () => {
    const wrapper = await mountPanel(pinia);
    expect(wrapper.find('[data-testid="logs-directory-text"]').text()).toContain('D:\\HaloAgentData\\logs');
    expect(wrapper.find('[data-testid="last-modified-text"]').text()).toContain('路径最后修改：');
  });

  it('回退态显示告警，正常态不显示', async () => {
    const wrapper = await mountPanel(pinia);
    expect(wrapper.find('[data-testid="fallback-warning"]').exists()).toBe(false);
    useSystemStore().storageConfig = { ...baseConfig, isUsingFallback: true } as never;
    await wrapper.vm.$nextTick();
    expect(wrapper.find('[data-testid="fallback-warning"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="fallback-warning"]').text()).toContain(baseConfig.fallbackPath);
  });

  it('健康度三态：正常 / 权限受限 / 降级兜底', async () => {
    const wrapper = await mountPanel(pinia);
    const tag = () => wrapper.find('[data-testid="storage-health-tag"]').text();
    expect(tag()).toContain('健康');
    useSystemStore().storageConfig = { ...baseConfig, isWritable: false } as never;
    await wrapper.vm.$nextTick();
    expect(tag()).toContain('权限受限');
    useSystemStore().storageConfig = { ...baseConfig, isWritable: true, isUsingFallback: true } as never;
    await wrapper.vm.$nextTick();
    expect(tag()).toContain('降级兜底中');
  });

  it('磁盘总量为 0 时标注容量未知', async () => {
    useSystemStore().storageConfig = { ...baseConfig, diskTotalSpaceMb: 0, diskFreeSpaceMb: 0 } as never;
    const wrapper = await mountPanel(pinia);
    expect(wrapper.find('[data-testid="disk-usage-text"]').text()).toContain('容量未知');
  });

  it('存储分布区块在接口返回后渲染', async () => {
    bridgeStub.getStorageBreakdown.mockResolvedValue({
      ok: true,
      data: { rootPath: 'D:\\HaloAgentData', totalBytes: 4096, items: [{ name: 'logs', sizeBytes: 4096, fileCount: 2 }] },
    });
    const wrapper = await mountPanel(pinia);
    expect(wrapper.find('[data-testid="storage-breakdown"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="storage-breakdown"]').text()).toContain('logs');
  });

  it('存储分布数值列在大文件数下仍单行不换行', async () => {
    bridgeStub.getStorageBreakdown.mockResolvedValue({
      ok: true,
      data: {
        rootPath: 'D:\\HaloAgentData',
        totalBytes: 4096,
        items: [{ name: 'backups', sizeBytes: 4096, fileCount: 123456 }],
      },
    });
    const wrapper = await mountPanel(pinia);
    const value = wrapper.find('[data-testid="breakdown-value-backups"]');
    expect(value.exists()).toBe(true);
    expect(value.classes()).toContain('whitespace-nowrap');
    expect(value.text()).toContain('123456 个');
  });

  it('用量库不支持时整卡显示提示，不崩溃', async () => {
    bridgeStub.getUsageDbStatus.mockResolvedValue({ ok: false, code: 'USAGE_UNSUPPORTED', message: '通道不可用' });
    const wrapper = await mountPanel(pinia);
    expect(wrapper.find('[data-testid="usage-db-unsupported"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="usage-db-status"]').exists()).toBe(false);
  });

  it('附件统计默认 dryRun=true，报告展示超期数量', async () => {
    const wrapper = await mountPanel(pinia);
    await wrapper.find('[data-testid="attachment-stats-btn"]').trigger('click');
    await flushPromises();
    expect(bridgeStub.cleanupImageAttachments).toHaveBeenCalledWith(30, true);
    expect(wrapper.find('[data-testid="attachment-report"]').text()).toContain('超期：3 个');
  });

  it('确认后才以 dryRun=false 执行附件清理（防误删）', async () => {
    const wrapper = await mountPanel(pinia);
    await wrapper.find('[data-testid="attachment-stats-btn"]').trigger('click');
    await flushPromises();
    await wrapper.find('[data-testid="attachment-purge-btn"]').trigger('click');
    await flushPromises();
    expect(bridgeStub.cleanupImageAttachments).toHaveBeenLastCalledWith(30, false);
  });

  it('清理过期日志把当前保留天数传给 cleanupLogs', async () => {
    const wrapper = await mountPanel(pinia);
    const daysInput = wrapper.find('[data-testid="retention-days-input"] input');
    await daysInput.setValue('21');
    const buttons = wrapper.findAll('button');
    const cleanupBtn = buttons.find((b) => b.text().includes('清理过期日志'));
    expect(cleanupBtn).toBeTruthy();
    await cleanupBtn!.trigger('click');
    await flushPromises();
    expect(bridgeStub.cleanupExpiredLogs).toHaveBeenCalledWith(21);
  });

  it('保留天数失焦调用 setLogRetentionDays（只设置不清理）', async () => {
    const wrapper = await mountPanel(pinia);
    const daysInput = wrapper.find('[data-testid="retention-days-input"] input');
    await daysInput.setValue('7');
    await daysInput.trigger('blur');
    await flushPromises();
    expect(bridgeStub.setLogRetentionDays).toHaveBeenCalledWith(7);
    expect(bridgeStub.cleanupExpiredLogs).not.toHaveBeenCalled();
  });

  it('事件时间线空态展示提示', async () => {
    const wrapper = await mountPanel(pinia);
    expect(wrapper.find('[data-testid="storage-events-empty"]').exists()).toBe(true);
  });
});
