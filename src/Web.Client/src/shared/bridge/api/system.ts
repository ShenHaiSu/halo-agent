/** need21-F02: 系统域 API（旧 client.ts wrapNativeHostBridge 系统/存储/技能/用量/活跃态方法迁移，签名不变）。 */
import type { BridgeTransport } from '../transport/transport';
import type {
  AppearanceSettingsDto,
  BridgeResponse,
  HostBridgeApi,
  SkillMemoryDetail,
  SkillMemoryKind,
  SkillMemoryListItem,
  SkillMemoryOpResult,
  SkillMemoryScope,
  SkillMemoryScopeFilter,
  StorageBreakdown,
  StorageConfigInfo,
  StorageEventDto,
  StorageMigrationResult,
  StorageValidationResult,
  SystemRuntimeInfo,
  UiActiveState,
  UsageDbStatus,
  UsageFilter,
  UsageFilterOptions,
  UsageOverview,
  UsagePage,
  UsageProviderShareItem,
  UsageTrend,
} from '../protocol/types';
import { normalizeBridgePayload } from './normalize';

export function createSystemApi(
  t: BridgeTransport
): Pick<
  HostBridgeApi,
  | 'getSystemInfo'
  | 'getStorageConfig'
  | 'validateStoragePath'
  | 'selectFolderDialog'
  | 'changeStoragePath'
  | 'resetStoragePathToDefault'
  | 'openPathInExplorer'
  | 'openExternalLink'
  | 'cleanupExpiredLogs'
  | 'setLogRetentionDays'
  | 'writeDiagnosticLog'
  | 'setWindowTitle'
  | 'getStorageBreakdown'
  | 'getRecentHostEvents'
  | 'cleanupImageAttachments'
  | 'listSkillMemory'
  | 'getSkillMemoryDetail'
  | 'setSkillMemoryEnabled'
  | 'deleteSkillMemory'
  | 'openSkillMemoryFolder'
  | 'getUsageFilterOptions'
  | 'getUsageOverview'
  | 'getUsageTrend'
  | 'getUsageProviderShare'
  | 'queryUsagePage'
  | 'getUsageDbStatus'
  | 'getLastActive'
  | 'setLastActive'
  | 'getAppearance'
  | 'setAppearance'
> {
  return {
    async getSystemInfo() {
      return normalizeBridgePayload<BridgeResponse<SystemRuntimeInfo>>(await t.call('getSystemInfo'));
    },
    async getStorageConfig() {
      return normalizeBridgePayload<BridgeResponse<StorageConfigInfo>>(await t.call('getStorageConfig'));
    },
    async validateStoragePath(targetPath: string) {
      return normalizeBridgePayload<BridgeResponse<StorageValidationResult>>(await t.call('validateStoragePath', targetPath));
    },
    async selectFolderDialog(initialPath?: string) {
      return normalizeBridgePayload<BridgeResponse<string | null>>(await t.call('selectFolderDialog', initialPath ?? ''));
    },
    async changeStoragePath(newPath: string, migrateData: boolean) {
      return normalizeBridgePayload<BridgeResponse<StorageMigrationResult>>(await t.call('changeStoragePath', newPath, migrateData));
    },
    async resetStoragePathToDefault(migrateData: boolean) {
      return normalizeBridgePayload<BridgeResponse<StorageMigrationResult>>(await t.call('resetStoragePathToDefault', migrateData));
    },
    async openPathInExplorer(targetPath: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await t.call('openPathInExplorer', targetPath));
    },
    async openExternalLink(href: string, workspaceRoot?: string) {
      if (t.supports('openExternalLink')) {
        return normalizeBridgePayload<BridgeResponse<boolean>>(await t.call('openExternalLink', href, workspaceRoot ?? ''));
      }
      // 旧后端兼容：http(s) 用默认方式打开，本地路径退化为 explorer 定位
      if (/^https?:\/\//i.test(String(href || ''))) {
        try {
          window.open(href, '_blank', 'noopener');
        } catch (e) {
          console.warn('[bridge] window.open fallback failed:', (e as Error)?.message ?? e);
        }
        return { ok: true, data: true };
      }
      return normalizeBridgePayload<BridgeResponse<boolean>>(await t.call('openPathInExplorer', href));
    },
    async cleanupExpiredLogs(retentionDays = 14) {
      return normalizeBridgePayload<BridgeResponse<number>>(await t.call('cleanupExpiredLogs', retentionDays));
    },
    async setLogRetentionDays(retentionDays: number) {
      try {
        if (!t.supports('setLogRetentionDays')) return { ok: false, code: 'STORAGE_UNSUPPORTED', message: '设置保留天数通道不可用' };
        return normalizeBridgePayload<BridgeResponse<number>>(await t.call('setLogRetentionDays', retentionDays));
      } catch (e) {
        console.warn('[bridge] setLogRetentionDays unsupported:', (e as Error)?.message ?? e);
        return { ok: false, code: 'STORAGE_UNSUPPORTED', message: '设置保留天数通道不可用' };
      }
    },
    async getStorageBreakdown() {
      try {
        if (!t.supports('getStorageBreakdown')) return { ok: false, code: 'STORAGE_UNSUPPORTED', message: '存储分布通道不可用' };
        return normalizeBridgePayload<BridgeResponse<StorageBreakdown>>(await t.call('getStorageBreakdown'));
      } catch (e) {
        console.warn('[bridge] getStorageBreakdown unsupported:', (e as Error)?.message ?? e);
        return { ok: false, code: 'STORAGE_UNSUPPORTED', message: '存储分布通道不可用' };
      }
    },
    async getRecentHostEvents(limit = 20) {
      try {
        if (!t.supports('getRecentHostEvents')) return { ok: false, code: 'STORAGE_UNSUPPORTED', message: '历史事件通道不可用' };
        return normalizeBridgePayload<BridgeResponse<StorageEventDto[]>>(await t.call('getRecentHostEvents', limit));
      } catch (e) {
        console.warn('[bridge] getRecentHostEvents unsupported:', (e as Error)?.message ?? e);
        return { ok: false, code: 'STORAGE_UNSUPPORTED', message: '历史事件通道不可用' };
      }
    },
    async cleanupImageAttachments(retentionDays = 30, dryRun = true) {
      try {
        if (!t.supports('cleanupImageAttachments')) return { ok: false, code: 'STORAGE_UNSUPPORTED', message: '附件清理通道不可用' };
        return normalizeBridgePayload<BridgeResponse<string>>(await t.call('cleanupImageAttachments', retentionDays, dryRun));
      } catch (e) {
        console.warn('[bridge] cleanupImageAttachments unsupported:', (e as Error)?.message ?? e);
        return { ok: false, code: 'STORAGE_UNSUPPORTED', message: '附件清理通道不可用' };
      }
    },
    async writeDiagnosticLog(channel: string, message: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await t.call('writeDiagnosticLog', channel, message));
    },
    async setWindowTitle(title: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await t.call('setWindowTitle', title));
    },
    async listSkillMemory(kind: SkillMemoryKind, scope: SkillMemoryScopeFilter, workspaceId?: string) {
      try {
        if (!t.supports('listSkillMemory')) return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' };
        return normalizeBridgePayload<BridgeResponse<SkillMemoryListItem[]>>(
          await t.call('listSkillMemory', kind ?? 'skill', scope ?? 'all', workspaceId ?? '')
        );
      } catch (e) {
        console.warn('[bridge] SKILL_UNSUPPORTED:', (e as Error)?.message ?? e);
        return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' };
      }
    },
    async getSkillMemoryDetail(kind: SkillMemoryKind, scope: SkillMemoryScope, name: string, workspaceId?: string) {
      try {
        if (!t.supports('getSkillMemoryDetail')) return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' };
        return normalizeBridgePayload<BridgeResponse<SkillMemoryDetail>>(
          await t.call('getSkillMemoryDetail', kind ?? 'skill', scope ?? 'global', name ?? '', workspaceId ?? '')
        );
      } catch (e) {
        console.warn('[bridge] SKILL_UNSUPPORTED:', (e as Error)?.message ?? e);
        return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' };
      }
    },
    async setSkillMemoryEnabled(kind: SkillMemoryKind, scope: SkillMemoryScope, name: string, enabled: boolean, workspaceId?: string) {
      try {
        if (!t.supports('setSkillMemoryEnabled')) return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' };
        return normalizeBridgePayload<BridgeResponse<SkillMemoryOpResult>>(
          await t.call('setSkillMemoryEnabled', kind ?? 'skill', scope ?? 'global', name ?? '', enabled ?? true, workspaceId ?? '')
        );
      } catch (e) {
        console.warn('[bridge] SKILL_UNSUPPORTED:', (e as Error)?.message ?? e);
        return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' };
      }
    },
    async deleteSkillMemory(kind: SkillMemoryKind, scope: SkillMemoryScope, name: string, workspaceId?: string) {
      try {
        if (!t.supports('deleteSkillMemory')) return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' };
        return normalizeBridgePayload<BridgeResponse<SkillMemoryOpResult>>(
          await t.call('deleteSkillMemory', kind ?? 'skill', scope ?? 'global', name ?? '', workspaceId ?? '')
        );
      } catch (e) {
        console.warn('[bridge] SKILL_UNSUPPORTED:', (e as Error)?.message ?? e);
        return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' };
      }
    },
    async openSkillMemoryFolder(kind: SkillMemoryKind, scope: SkillMemoryScope, name: string, workspaceId?: string) {
      try {
        if (!t.supports('openSkillMemoryFolder')) return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' };
        return normalizeBridgePayload<BridgeResponse<boolean>>(
          await t.call('openSkillMemoryFolder', kind ?? 'skill', scope ?? 'global', name ?? '', workspaceId ?? '')
        );
      } catch (e) {
        console.warn('[bridge] SKILL_UNSUPPORTED:', (e as Error)?.message ?? e);
        return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' };
      }
    },
    async getUsageFilterOptions() {
      try {
        if (!t.supports('getUsageFilterOptions')) return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' };
        return normalizeBridgePayload<BridgeResponse<UsageFilterOptions>>(await t.call('getUsageFilterOptions'));
      } catch (e) {
        console.warn('[bridge] USAGE_UNSUPPORTED:', (e as Error)?.message ?? e);
        return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' };
      }
    },
    async getUsageOverview(filter: UsageFilter | string) {
      try {
        if (!t.supports('getUsageOverview')) return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' };
        const payload = typeof filter === 'string' ? filter : JSON.stringify(filter ?? {});
        return normalizeBridgePayload<BridgeResponse<UsageOverview>>(await t.call('getUsageOverview', payload));
      } catch (e) {
        console.warn('[bridge] USAGE_UNSUPPORTED:', (e as Error)?.message ?? e);
        return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' };
      }
    },
    async getUsageTrend(filter: UsageFilter | string, metric?: string) {
      try {
        if (!t.supports('getUsageTrend')) return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' };
        const payload = typeof filter === 'string' ? filter : JSON.stringify(filter ?? {});
        return normalizeBridgePayload<BridgeResponse<UsageTrend>>(await t.call('getUsageTrend', payload, metric ?? 'total'));
      } catch (e) {
        console.warn('[bridge] USAGE_UNSUPPORTED:', (e as Error)?.message ?? e);
        return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' };
      }
    },
    async getUsageProviderShare(filter: UsageFilter | string) {
      try {
        if (!t.supports('getUsageProviderShare')) return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' };
        const payload = typeof filter === 'string' ? filter : JSON.stringify(filter ?? {});
        return normalizeBridgePayload<BridgeResponse<UsageProviderShareItem[]>>(await t.call('getUsageProviderShare', payload));
      } catch (e) {
        console.warn('[bridge] USAGE_UNSUPPORTED:', (e as Error)?.message ?? e);
        return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' };
      }
    },
    async queryUsagePage(filter: UsageFilter | string, page: number, pageSize: number) {
      try {
        if (!t.supports('queryUsagePage')) return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' };
        const payload = typeof filter === 'string' ? filter : JSON.stringify(filter ?? {});
        return normalizeBridgePayload<BridgeResponse<UsagePage>>(await t.call('queryUsagePage', payload, page ?? 1, pageSize ?? 10));
      } catch (e) {
        console.warn('[bridge] USAGE_UNSUPPORTED:', (e as Error)?.message ?? e);
        return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' };
      }
    },
    async getUsageDbStatus() {
      try {
        if (!t.supports('getUsageDbStatus')) return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' };
        return normalizeBridgePayload<BridgeResponse<UsageDbStatus>>(await t.call('getUsageDbStatus'));
      } catch (e) {
        console.warn('[bridge] USAGE_UNSUPPORTED:', (e as Error)?.message ?? e);
        return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' };
      }
    },
    async getLastActive() {
      try {
        if (!t.supports('getLastActive')) return { ok: false, code: 'USAGE_UNSUPPORTED', message: '活跃状态通道不可用' };
        return normalizeBridgePayload<BridgeResponse<UiActiveState>>(await t.call('getLastActive'));
      } catch (e) {
        console.warn('[bridge] UISTATE_UNSUPPORTED:', (e as Error)?.message ?? e);
        return { ok: false, code: 'USAGE_UNSUPPORTED', message: '活跃状态通道不可用' };
      }
    },
    async setLastActive(workspaceId: string, sessionId: string) {
      try {
        if (!t.supports('setLastActive')) return { ok: false, code: 'USAGE_UNSUPPORTED', message: '活跃状态通道不可用' };
        return normalizeBridgePayload<BridgeResponse<boolean>>(await t.call('setLastActive', workspaceId ?? '', sessionId ?? ''));
      } catch (e) {
        console.warn('[bridge] UISTATE_UNSUPPORTED:', (e as Error)?.message ?? e);
        return { ok: false, code: 'USAGE_UNSUPPORTED', message: '活跃状态通道不可用' };
      }
    },
    async getAppearance() {
      try {
        if (!t.supports('getAppearance')) return { ok: false, code: 'APPEARANCE_UNSUPPORTED', message: '外观配置通道不可用' };
        return normalizeBridgePayload<BridgeResponse<AppearanceSettingsDto>>(await t.call('getAppearance'));
      } catch (e) {
        console.warn('[bridge] APPEARANCE_UNSUPPORTED:', (e as Error)?.message ?? e);
        return { ok: false, code: 'APPEARANCE_UNSUPPORTED', message: '外观配置通道不可用' };
      }
    },
    async setAppearance(settingsJson: string) {
      try {
        if (!t.supports('setAppearance')) return { ok: false, code: 'APPEARANCE_UNSUPPORTED', message: '外观配置通道不可用' };
        return normalizeBridgePayload<BridgeResponse<boolean>>(await t.call('setAppearance', settingsJson));
      } catch (e) {
        console.warn('[bridge] APPEARANCE_UNSUPPORTED:', (e as Error)?.message ?? e);
        return { ok: false, code: 'APPEARANCE_UNSUPPORTED', message: '外观配置通道不可用' };
      }
    },
  };
}
