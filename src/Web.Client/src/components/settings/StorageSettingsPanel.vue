<template>
  <div class="space-y-4 min-w-0 max-w-full overflow-x-hidden">
    <!-- 1. 存储路径管理卡片 -->
    <n-card title="数据与日志根目录配置" size="small">
      <div class="space-y-3">
        <n-alert type="info" :show-icon="true">
          <template #icon>
            <FileCode2 class="w-4 h-4" />
          </template>
          <div class="text-xs leading-relaxed">
            <div>
              固定引导指针文件位置：<code class="setting-inset px-1.5 py-0.5 font-mono break-all">%LocalAppData%\HaloAgent\bootstrap.json</code>
            </div>
            <div class="text-secondary mt-0.5 font-mono break-all">
              实际路径：{{ storageConfig?.bootstrapFilePath || '加载中...' }}
            </div>
          </div>
        </n-alert>

        <!-- 当前生效路径展示栏 -->
        <div class="space-y-1.5">
          <div class="flex items-center justify-between">
            <span class="text-xs font-medium text-secondary">当前生效数据根目录 (DataRootPath)</span>
            <n-tag
              :type="storageConfig?.isWritable !== false ? 'success' : 'error'"
              size="small"
              :bordered="false"
            >
              <div class="flex items-center gap-1.5">
                <CheckCircle2 v-if="storageConfig?.isWritable !== false" class="w-3.5 h-3.5" />
                <AlertTriangle v-else class="w-3.5 h-3.5" />
                <span>
                  {{ storageConfig?.isWritable !== false ? '可读写' : '权限受限' }} · 剩余空间
                  {{ systemStore.diskFreeGbText }} GB
                </span>
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

          <div
            v-if="storageConfig?.previousDataRootPath"
            class="text-[11px] text-secondary font-mono flex items-center gap-1.5"
          >
            <History class="w-3 h-3" />
            <span>上次路径：{{ storageConfig.previousDataRootPath }}</span>
          </div>
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

          <n-button
            secondary
            size="medium"
            @click="handleOpenInExplorer(storageConfig?.dataRootPath)"
          >
            <template #icon>
              <ExternalLink class="w-4 h-4" />
            </template>
            在资源管理器中打开数据目录
          </n-button>

          <n-button
            secondary
            size="medium"
            :disabled="isDefaultPath || systemStore.migrating"
            @click="handleResetToDefaultCDrive"
          >
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
            清理过期日志 ({{ storageConfig?.logRetentionDays ?? 14 }}天前)
          </n-button>
          <n-button
            size="small"
            type="primary"
            ghost
            @click="handleOpenInExplorer(systemStore.systemInfo?.logsDirectory)"
          >
            <template #icon>
              <FolderOpen class="w-3.5 h-3.5" />
            </template>
            打开日志目录
          </n-button>
        </n-space>
      </template>

      <n-descriptions bordered :column="1" size="small" label-placement="top">
        <n-descriptions-item
          v-for="ch in displayedLogChannels"
          :key="ch.channel"
          :label="ch.label"
        >
          <div class="space-y-1">
            <div class="font-mono text-xs font-medium text-primary flex items-center justify-between">
              <span>{{ ch.fileName }}</span>
              <n-tag :type="ch.exists ? 'success' : 'default'" size="small" :bordered="false">
                {{ formatBytes(ch.sizeBytes) }}
              </n-tag>
            </div>
            <div class="text-[11px] text-secondary truncate max-w-full font-mono" :title="ch.filePath">
              {{ ch.filePath }}
            </div>
          </div>
        </n-descriptions-item>
      </n-descriptions>
    </n-card>

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
        </div>

        <div class="space-y-2">
          <div class="text-xs text-secondary font-medium">请选择数据迁移策略：</div>
          <n-radio-group
            v-model:value="migrateExistingData"
            name="migration-mode"
            :disabled="systemStore.migrating"
          >
            <div class="space-y-2.5">
              <div
                class="setting-inset p-2.5 cursor-pointer min-w-0"
                @click="!systemStore.migrating && (migrateExistingData = true)"
              >
                <n-radio :value="true">
                  <span class="text-xs font-medium">
                    将当前目录下的所有历史会话、Provider 配置及日志完整迁移至新路径并切换（推荐）
                  </span>
                </n-radio>
              </div>
              <div
                class="setting-inset p-2.5 cursor-pointer min-w-0"
                @click="!systemStore.migrating && (migrateExistingData = false)"
              >
                <n-radio :value="false">
                  <span class="text-xs font-medium">
                    仅切换至新路径（初始化空白配置，保留原目录旧文件不动）
                  </span>
                </n-radio>
              </div>
            </div>
          </n-radio-group>
        </div>

        <div v-if="systemStore.migrating" class="space-y-1.5 pt-1">
          <n-progress
            type="line"
            :percentage="systemStore.migrationPercent"
            :indicator-placement="'inside'"
            processing
          />
          <div class="text-xs text-secondary font-mono">
            {{ systemStore.migrationStatusText }}
          </div>
        </div>
      </div>

      <template #action>
        <n-space :size="8">
          <n-button
            size="medium"
            :disabled="systemStore.migrating"
            @click="showMigrationDialog = false"
          >
            取消
          </n-button>
          <n-button
            type="primary"
            size="medium"
            :loading="systemStore.migrating"
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
import { ref, computed } from 'vue';
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
} from 'lucide-vue-next';
import { useSystemStore } from '@/stores/systemStore';

const systemStore = useSystemStore();
const message = useMessage();

const showMigrationDialog = ref(false);
const pendingTargetPath = ref('');
const migrateExistingData = ref(true);

const storageConfig = computed(() => systemStore.storageConfig);

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
      filePath: `C:\\HaloAgentData\\logs\\app-${today}.log`,
      sizeBytes: 0,
      exists: false,
    },
    {
      channel: 'llm',
      label: '大模型请求审计 (LLM Trace)',
      fileName: `llm-trace-${today}.log`,
      filePath: `C:\\HaloAgentData\\logs\\llm-trace-${today}.log`,
      sizeBytes: 0,
      exists: false,
    },
    {
      channel: 'tool',
      label: '工具执行日志 (Tool Exec)',
      fileName: `tool-exec-${today}.log`,
      filePath: `C:\\HaloAgentData\\logs\\tool-exec-${today}.log`,
      sizeBytes: 0,
      exists: false,
    },
  ];
});

function formatBytes(bytes: number): string {
  if (!bytes || bytes <= 0) return '0 B';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
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
  } else {
    message.error(res.message || '存储路径切换失败，请检查目录权限与剩余空间');
  }
}

async function handleResetToDefaultCDrive() {
  const res = await systemStore.resetToDefaultCDrive(true);
  if (res.ok && res.data?.success) {
    message.success(`已恢复默认存储路径：${res.data.newDataRootPath}`);
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
  await systemStore.writeTestLogEntry(
    'llm',
    'diagnostic check: Authorization: Bearer sk-1234567890abcdef9876 masking test.'
  );
  await systemStore.writeTestLogEntry('tool', 'diagnostic check: tool channel write test.');
  message.success('已向 App / LLM / Tool 三通道写入诊断日志并自动脱敏');
}

async function handleCleanupExpiredLogs() {
  const days = storageConfig.value?.logRetentionDays ?? 14;
  const res = await systemStore.cleanupLogs(days);
  if (res.ok) {
    message.success(res.message || `已清理过期日志（共移除 ${res.data ?? 0} 个文件）`);
  } else {
    message.error(res.message || '清理过期日志失败');
  }
}
</script>
