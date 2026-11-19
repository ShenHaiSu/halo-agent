<template>
  <div class="space-y-4 min-w-0 max-w-full overflow-x-hidden">
    <!-- 1. 存储路径管理卡片 -->
    <n-card title="数据与日志根目录配置" size="small">
      <div class="space-y-3">
        <!-- 回退告警：主路径不可写时明确告知实际写入位置 -->
        <n-alert v-if="storageConfig?.isUsingFallback" type="warning" :show-icon="false" data-testid="fallback-warning">
          <template #icon>
            <AlertTriangle class="w-4 h-4" />
          </template>
          当前正在使用兜底目录（主路径不可写），数据将写入：
          <code class="font-mono break-all">{{ storageConfig.fallbackPath }}</code>
        </n-alert>

        <n-alert type="info" :show-icon="true">
          <template #icon>
            <FileCode2 class="w-4 h-4" />
          </template>
          <div class="text-xs leading-relaxed">
            <div>
              固定引导指针文件位置：<code class="setting-inset px-1.5 py-0.5 font-mono break-all"
                >%LocalAppData%\HaloAgent\bootstrap.json</code
              >
            </div>
            <div class="text-secondary mt-0.5 font-mono break-all">实际路径：{{ storageConfig?.bootstrapFilePath || '加载中...' }}</div>
          </div>
        </n-alert>

        <!-- 当前生效路径展示栏 -->
        <div class="space-y-1.5">
          <div class="flex items-center justify-between flex-wrap gap-2">
            <span class="text-xs font-medium text-secondary">当前生效数据根目录 (DataRootPath)</span>
            <n-tag :type="healthTagType(health)" size="small" :bordered="false" data-testid="storage-health-tag">
              <div class="flex items-center gap-1.5">
                <CheckCircle2 v-if="health === 'healthy'" class="w-3.5 h-3.5" />
                <AlertTriangle v-else class="w-3.5 h-3.5" />
                <span>{{ healthLabel(health) }} · 剩余空间 {{ systemStore.diskFreeGbText }} GB</span>
              </div>
            </n-tag>
          </div>

          <n-input
            :value="storageConfig?.dataRootPath || 'C:\\HaloAgentData'"
            readonly
            size="medium"
            class="font-mono text-xs"
            data-testid="current-data-root-input"
          >
            <template #prefix>
              <HardDrive class="w-4 h-4 text-secondary mr-1" />
            </template>
          </n-input>

          <div class="text-[11px] text-secondary font-mono" data-testid="last-modified-text">
            路径最后修改：{{ formatLastWrite(storageConfig?.lastModifiedUtc) }}
          </div>

          <div v-if="storageConfig?.previousDataRootPath" class="text-[11px] text-secondary font-mono flex items-center gap-1.5">
            <History class="w-3 h-3" />
            <span>上次路径：{{ storageConfig.previousDataRootPath }}</span>
          </div>

          <!-- 磁盘占用率：总量为 0 时按“容量未知”兜底 -->
          <div class="space-y-1 pt-1">
            <n-progress
              type="line"
              :percentage="diskUsage.usedPercent"
              :indicator-placement="'inside'"
              :height="14"
              data-testid="disk-usage-progress"
            />
            <div class="text-[11px] text-secondary font-mono" data-testid="disk-usage-text">
              <template v-if="diskUsage.capacityUnknown">容量未知（网络盘或权限受限，无法读取磁盘总量）</template>
              <template v-else>
                已用 {{ diskUsage.usedGbText }} GB / 共 {{ diskUsage.totalGbText }} GB（可用 {{ diskUsage.freeGbText }} GB）
              </template>
            </div>
          </div>
        </div>

        <!-- 存储分布：按受管子目录聚合，占比用进度条呈现 -->
        <div v-if="breakdown" class="space-y-1.5 border-t border-subtle pt-2.5" data-testid="storage-breakdown">
          <div class="text-xs font-medium text-secondary flex items-center gap-1.5">
            <PieChart class="w-3.5 h-3.5" />
            存储分布：<span class="font-mono break-all">{{ breakdown.rootPath }}</span>
          </div>
          <div v-for="item in breakdown.items" :key="item.name" class="flex items-center gap-2">
            <span class="w-20 shrink-0 text-[11px] font-mono text-secondary">{{ item.name }}</span>
            <n-progress type="line" :percentage="breakdownPercent(item.sizeBytes)" :height="10" :show-indicator="false" class="flex-1" />
            <span class="w-28 shrink-0 text-right text-[11px] font-mono">
              {{ formatBytes(item.sizeBytes) }} / {{ item.fileCount }} 个
            </span>
          </div>
          <div class="text-[11px] text-secondary font-mono">合计：{{ formatBytes(breakdown.totalBytes) }}</div>
        </div>

        <!-- 操作按钮组 -->
        <n-space align="center" :size="10">
          <n-button
            type="primary"
            size="medium"
            :loading="systemStore.migrating"
            data-testid="change-storage-path-btn"
            @click="handleChangeStoragePath"
          >
            <template #icon>
              <FolderSync class="w-4 h-4" />
            </template>
            修改存储目录
          </n-button>

          <n-button secondary size="medium" @click="handleOpenInExplorer(storageConfig?.dataRootPath)">
            <template #icon>
              <ExternalLink class="w-4 h-4" />
            </template>
            在资源管理器中打开数据目录
          </n-button>

          <n-button secondary size="medium" :disabled="isDefaultPath || systemStore.migrating" @click="handleResetToDefaultCDrive">
            <template #icon>
              <RotateCcw class="w-4 h-4" />
            </template>
            恢复默认 C:\HaloAgentData
          </n-button>
        </n-space>
      </div>
    </n-card>

    <!-- 2. 三通道分级日志监控区 -->
    <n-card title="三通道分级滚动日志监控" size="small">
      <template #header-extra>
        <n-space :size="8" class="flex flex-wrap">
          <n-button size="small" secondary @click="handleWriteDiagnosticLogs">
            <template #icon>
              <FilePlus2 class="w-3.5 h-3.5" />
            </template>
            写入测试日志
          </n-button>
          <n-button size="small" secondary @click="handleCleanupExpiredLogs">
            <template #icon>
              <Trash2 class="w-3.5 h-3.5" />
            </template>
            清理过期日志 ({{ retentionDays }}天前)
          </n-button>
          <n-button size="small" type="primary" ghost @click="handleOpenInExplorer(systemStore.systemInfo?.logsDirectory)">
            <template #icon>
              <FolderOpen class="w-3.5 h-3.5" />
            </template>
            打开日志目录
          </n-button>
        </n-space>
      </template>

      <div class="space-y-2 pb-2 border-b border-subtle">
        <div class="text-[11px] text-secondary font-mono break-all" data-testid="logs-directory-text">
          日志目录：{{ systemStore.systemInfo?.logsDirectory || '加载中...' }}
        </div>
        <div class="flex items-center gap-2 flex-wrap">
          <span class="text-[11px] text-secondary">日志保留天数：</span>
          <n-input-number
            v-model:value="retentionDays"
            :min="1"
            :max="365"
            size="small"
            class="w-24"
            data-testid="retention-days-input"
            @blur="handleRetentionDaysCommit"
          />
          <span class="text-[11px] text-secondary">失焦保存（只写设置不删文件）；清理为独立动作</span>
        </div>
      </div>

      <n-descriptions bordered :column="1" size="small" label-placement="top" class="pt-2">
        <n-descriptions-item v-for="ch in displayedLogChannels" :key="ch.channel" :label="ch.label">
          <div class="space-y-1">
            <div class="font-mono text-xs font-medium text-primary flex items-center justify-between">
              <span>{{ ch.fileName }}</span>
              <n-tag :type="channelTagType(ch)" size="small" :bordered="false" :data-testid="`log-size-tag-${ch.channel}`">
                {{ channelSizeText(ch) }}
              </n-tag>
            </div>
            <div class="text-[11px] text-secondary truncate max-w-full font-mono" :title="ch.filePath">
              {{ ch.filePath }}
            </div>
            <div class="text-[11px] text-secondary" :data-testid="`log-last-write-${ch.channel}`">
              最后写入：{{ formatLastWrite(ch.lastWriteTimeUtc) }}
            </div>
          </div>
        </n-descriptions-item>
      </n-descriptions>
    </n-card>

    <!-- 3. 用量库状态 + 附件清理 -->
    <StorageDiagnosticCard />

    <!-- 4. 最近存储事件（内存态 + 可加载持久历史） -->
    <StorageEventTimelineCard />

    <!-- 迁移模式选择与进度确认弹窗 -->
    <n-modal
      v-model:show="showMigrationDialog"
      preset="dialog"
      title="确认切换数据与日志存储目录"
      :mask-closable="!systemStore.migrating"
      :closable="!systemStore.migrating"
      style="width: min(540px, 90vw)"
    >
      <div class="space-y-4 pt-2">
        <div class="space-y-1.5">
          <div class="text-xs text-secondary">目标存储路径（可由文件夹选择器选定或手动调整）：</div>
          <n-input
            v-model:value="pendingTargetPath"
            size="medium"
            placeholder="例如 D:\HaloAgentData 或 C:\HaloAgentData_Test"
            :disabled="systemStore.migrating"
            class="font-mono text-xs"
            data-testid="target-storage-path-input"
          />
          <div v-if="validating" class="text-[11px] text-secondary">正在校验目标路径...</div>
          <div v-else-if="validationResult" class="setting-inset p-2.5 text-[11px] space-y-1" data-testid="path-validation-result">
            <div class="flex items-start gap-1.5" :class="validationResult.isValid ? 'text-emerald-400' : 'text-red-400'">
              <CheckCircle2 v-if="validationResult.isValid" class="w-3.5 h-3.5 shrink-0 mt-0.5" />
              <AlertTriangle v-else class="w-3.5 h-3.5 shrink-0 mt-0.5" />
              <span>{{ validationResult.message }}</span>
            </div>
            <div v-if="validationResult.targetNonEmpty" class="text-amber-400" data-testid="validation-nonempty-warning">
              目标非空，完整迁移可能覆盖同名文件。
            </div>
            <div class="text-secondary font-mono">可用空间：{{ validationResult.availableFreeSpaceMb }} MB</div>
            <div
              v-if="validationResult.requiredSpaceMb > validationResult.availableFreeSpaceMb"
              class="text-red-400"
              data-testid="validation-insufficient-warning"
            >
              迁移预估所需 {{ validationResult.requiredSpaceMb }} MB 超过可用空间，请先清理目标磁盘。
            </div>
          </div>
        </div>

        <div class="space-y-2">
          <div class="text-xs text-secondary font-medium">请选择数据迁移策略：</div>
          <n-radio-group v-model:value="migrateExistingData" name="migration-mode" :disabled="systemStore.migrating">
            <div class="space-y-2.5">
              <div class="setting-inset p-2.5 cursor-pointer min-w-0" @click="!systemStore.migrating && (migrateExistingData = true)">
                <n-radio :value="true">
                  <span class="text-xs font-medium"> 将当前目录下的所有历史会话、Provider 配置及日志完整迁移至新路径并切换（推荐） </span>
                </n-radio>
              </div>
              <div class="setting-inset p-2.5 cursor-pointer min-w-0" @click="!systemStore.migrating && (migrateExistingData = false)">
                <n-radio :value="false">
                  <span class="text-xs font-medium"> 仅切换至新路径（初始化空白配置，保留原目录旧文件不动） </span>
                </n-radio>
              </div>
            </div>
          </n-radio-group>
        </div>

        <div v-if="systemStore.migrating" class="space-y-1.5 pt-1">
          <n-progress type="line" :percentage="systemStore.migrationPercent" :indicator-placement="'inside'" processing />
          <div class="text-xs text-secondary font-mono">
            {{ systemStore.migrationStatusText }}
          </div>
        </div>
      </div>

      <template #action>
        <n-space :size="8">
          <n-button size="medium" :disabled="systemStore.migrating" @click="showMigrationDialog = false"> 取消 </n-button>
          <n-button
            type="primary"
            size="medium"
            :loading="systemStore.migrating"
            :disabled="!canConfirmMigration"
            data-testid="confirm-storage-migration-btn"
            @click="confirmStoragePathChange"
          >
            立即执行切换
          </n-button>
        </n-space>
      </template>
    </n-modal>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, watch, onMounted, onUnmounted } from 'vue';
import { useMessage } from 'naive-ui';
import {
  FolderSync,
  ExternalLink,
  RotateCcw,
  HardDrive,
  CheckCircle2,
  AlertTriangle,
  FileCode2,
  FolderOpen,
  FilePlus2,
  Trash2,
  History,
  PieChart,
} from 'lucide-vue-next';
import { useSystemStore } from '@/stores/systemStore';
import { formatSmartBytesShort } from '@shared/utils/formatSmartBytes';
import type { StorageBreakdown, StorageValidationResult } from '@shared/bridge/hostBridge';
import {
  canConfirmStorageMigration,
  computeDiskUsage,
  computeStorageHealth,
  formatLastWrite,
  healthLabel,
  healthTagType,
  isLogChannelEmpty,
} from '../composables/storageDisplay';
import StorageDiagnosticCard from './StorageDiagnosticCard.vue';
import StorageEventTimelineCard from './StorageEventTimelineCard.vue';

const systemStore = useSystemStore();
const message = useMessage();

const showMigrationDialog = ref(false);
const pendingTargetPath = ref('');
const migrateExistingData = ref(true);
const retentionDays = ref(14);
const breakdown = ref<StorageBreakdown | null>(null);
const validationResult = ref<StorageValidationResult | null>(null);
const validating = ref(false);
let validationTimer: ReturnType<typeof setTimeout> | undefined;

const storageConfig = computed(() => systemStore.storageConfig);
const health = computed(() => computeStorageHealth(storageConfig.value?.isWritable, storageConfig.value?.isUsingFallback));
const diskUsage = computed(() => computeDiskUsage(storageConfig.value?.diskTotalSpaceMb ?? 0, storageConfig.value?.diskFreeSpaceMb ?? 0));

const isDefaultPath = computed(() => {
  const current = (storageConfig.value?.dataRootPath || '').toLowerCase().replace(/\\+$/, '');
  const def = (storageConfig.value?.defaultPath || 'C:\\HaloAgentData').toLowerCase().replace(/\\+$/, '');
  return current === def;
});

const displayedLogChannels = computed(() => {
  if (storageConfig.value?.logChannels?.length) {
    return storageConfig.value.logChannels;
  }
  const today = new Date().toISOString().slice(0, 10).replace(/-/g, '');
  return [
    {
      channel: 'app',
      label: '系统运行日志 (App)',
      fileName: `app-${today}.log`,
      filePath: '',
      sizeBytes: 0,
      exists: false,
      lastWriteTimeUtc: null,
    },
    {
      channel: 'llm',
      label: '大模型请求审计 (LLM Trace)',
      fileName: `llm-trace-${today}.log`,
      filePath: '',
      sizeBytes: 0,
      exists: false,
      lastWriteTimeUtc: null,
    },
    {
      channel: 'tool',
      label: '工具执行日志 (Tool Exec)',
      fileName: `tool-exec-${today}.log`,
      filePath: '',
      sizeBytes: 0,
      exists: false,
      lastWriteTimeUtc: null,
    },
  ];
});

// 预检未返回时不阻塞迁移（兼容无预检接口的老宿主）；返回无效或空间不足时禁用切换。
const canConfirmMigration = computed(() => canConfirmStorageMigration(pendingTargetPath.value, validating.value, validationResult.value));

watch(
  () => storageConfig.value?.logRetentionDays,
  (days) => {
    if (typeof days === 'number') retentionDays.value = days;
  },
  { immediate: true }
);

watch(pendingTargetPath, (value) => {
  if (validationTimer) clearTimeout(validationTimer);
  validationResult.value = null;
  if (!value.trim() || !showMigrationDialog.value) return;
  validationTimer = setTimeout(async () => {
    validating.value = true;
    try {
      const res = await systemStore.validateStoragePath(value.trim());
      validationResult.value = res.ok ? (res.data ?? null) : null;
    } catch {
      // 预检失败按“无结果”处理，不阻塞迁移主流程。
      validationResult.value = null;
    } finally {
      validating.value = false;
    }
  }, 300);
});

onMounted(() => {
  void loadBreakdown();
});
onUnmounted(() => {
  if (validationTimer) clearTimeout(validationTimer);
});

function formatBytes(bytes: number): string {
  return formatSmartBytesShort(bytes);
}

function breakdownPercent(sizeBytes: number): number {
  const total = breakdown.value?.totalBytes ?? 0;
  return total > 0 ? Math.min(100, Math.round((sizeBytes / total) * 100)) : 0;
}

function channelTagType(ch: { exists: boolean; sizeBytes: number }): 'success' | 'default' {
  return isLogChannelEmpty(ch.exists, ch.sizeBytes) ? 'default' : ch.exists ? 'success' : 'default';
}

function channelSizeText(ch: { exists: boolean; sizeBytes: number }): string {
  return isLogChannelEmpty(ch.exists, ch.sizeBytes) ? '空文件' : formatBytes(ch.sizeBytes);
}

async function loadBreakdown(): Promise<void> {
  try {
    const res = await systemStore.getStorageBreakdown();
    breakdown.value = res.ok ? (res.data ?? null) : null;
  } catch {
    // 存储分布为可选增强项，失败时隐藏该区块即可。
    breakdown.value = null;
  }
}

async function handleChangeStoragePath() {
  const selected = await systemStore.selectFolder(storageConfig.value?.dataRootPath);
  if (selected) {
    pendingTargetPath.value = selected;
    migrateExistingData.value = true;
    showMigrationDialog.value = true;
  }
}

async function confirmStoragePathChange() {
  const target = pendingTargetPath.value.trim();
  if (!target) {
    message.warning('请输入或选择有效的目标存储目录路径');
    return;
  }

  const res = await systemStore.changeStorageRoot(target, migrateExistingData.value);
  if (res.ok && res.data?.success) {
    message.success(res.data.message || `存储路径已切换至 ${res.data.newDataRootPath}`);
    showMigrationDialog.value = false;
    void loadBreakdown();
  } else {
    message.error(res.message || '存储路径切换失败，请检查目录权限与剩余空间');
  }
}

async function handleResetToDefaultCDrive() {
  const res = await systemStore.resetToDefaultCDrive(true);
  if (res.ok && res.data?.success) {
    message.success(`已恢复默认存储路径：${res.data.newDataRootPath}`);
    void loadBreakdown();
  } else {
    message.error(res.message || '恢复默认路径失败');
  }
}

async function handleOpenInExplorer(path?: string) {
  const res = await systemStore.openInExplorer(path);
  if (!res.ok) {
    message.error(res.message || '无法打开资源管理器');
  }
}

async function handleWriteDiagnosticLogs() {
  await systemStore.writeTestLogEntry('app', 'diagnostic check: App channel write test.');
  await systemStore.writeTestLogEntry('llm', 'diagnostic check: Authorization: Bearer sk-1234567890abcdef9876 masking test.');
  await systemStore.writeTestLogEntry('tool', 'diagnostic check: tool channel write test.');
  message.success('已向 App / LLM / Tool 三通道写入诊断日志并自动脱敏');
}

async function handleCleanupExpiredLogs() {
  const res = await systemStore.cleanupLogs(retentionDays.value);
  if (res.ok) {
    message.success(res.message || `已清理过期日志（共移除 ${res.data ?? 0} 个文件）`);
  } else {
    message.error(res.message || '清理过期日志失败');
  }
}

async function handleRetentionDaysCommit() {
  const days = retentionDays.value;
  if (!Number.isFinite(days) || days < 1 || days > 365) return;
  if (days === storageConfig.value?.logRetentionDays) return;
  const res = await systemStore.setLogRetentionDays(days);
  if (res.ok) {
    message.success(res.message || `日志保留天数已设为 ${days} 天`);
  } else {
    message.error(res.message || '设置日志保留天数失败');
  }
}
</script>
