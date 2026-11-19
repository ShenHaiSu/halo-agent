<template>
  <n-card title="用量库与附件诊断" size="small">
    <div class="space-y-3">
      <n-alert v-if="usageRes && !usageRes.ok" type="warning" :show-icon="false" data-testid="usage-db-unsupported">
        当前通道不支持用量库状态查询
      </n-alert>
      <n-descriptions v-else bordered :column="1" size="small" label-placement="top" data-testid="usage-db-status">
        <n-descriptions-item label="数据库路径">
          <span class="font-mono text-xs break-all">{{ usageRes?.data?.dbPath || '加载中...' }}</span>
        </n-descriptions-item>
        <n-descriptions-item label="记录数">{{ usageRes?.data?.recordCount ?? 0 }}</n-descriptions-item>
        <n-descriptions-item label="数据库大小">{{ formatBytes(usageRes?.data?.dbSizeBytes ?? 0) }}</n-descriptions-item>
        <n-descriptions-item label="最早 / 最近日期">
          {{ usageRes?.data?.oldestDate || '-' }} ~ {{ usageRes?.data?.newestDate || '-' }}
        </n-descriptions-item>
      </n-descriptions>

      <div class="border-t border-subtle pt-3 space-y-2">
        <div class="flex items-center gap-2 flex-wrap">
          <n-input-number v-model:value="attachmentDays" :min="1" :max="3650" size="small" class="w-28" />
          <span class="text-[11px] text-secondary">天内附件视为超期</span>
          <n-button size="small" secondary :loading="attachmentLoading" data-testid="attachment-stats-btn" @click="handleStats">
            <template #icon>
              <Files class="w-3.5 h-3.5" />
            </template>
            统计附件（不删除）
          </n-button>
          <n-button
            size="small"
            type="error"
            ghost
            :disabled="!canPurge"
            :loading="attachmentLoading"
            data-testid="attachment-purge-btn"
            @click="handlePurgeClick"
          >
            <template #icon>
              <Trash2 class="w-3.5 h-3.5" />
            </template>
            清理超期附件
          </n-button>
        </div>

        <div v-if="report" class="setting-inset p-2.5 text-[11px] space-y-1 font-mono" data-testid="attachment-report">
          <div>扫描：{{ report.scannedFiles }} 个 / {{ formatBytes(report.scannedBytes) }}</div>
          <div>超期：{{ report.expiredFiles }} 个 / {{ formatBytes(report.expiredBytes) }}</div>
          <div>{{ report.dryRun ? '当前为统计模式，未删除任何文件' : `已删除 ${report.deletedFiles} 个文件` }}</div>
        </div>
      </div>
    </div>
  </n-card>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { useMessage, useDialog } from 'naive-ui';
import { Files, Trash2 } from 'lucide-vue-next';
import { useSystemStore } from '@/stores/systemStore';
import { formatSmartBytesShort } from '@shared/utils/formatSmartBytes';
import { parseAttachmentReport, type AttachmentReportView } from '../composables/storageDisplay';
import type { BridgeResponse, UsageDbStatus } from '@shared/bridge/hostBridge';

const systemStore = useSystemStore();
const message = useMessage();
const dialog = useDialog();

const usageRes = ref<BridgeResponse<UsageDbStatus> | null>(null);
const attachmentDays = ref(30);
const attachmentLoading = ref(false);
const report = ref<AttachmentReportView | null>(null);
const canPurge = computed(() => !!report.value && report.value.expiredFiles > 0);

function formatBytes(bytes: number): string {
  return formatSmartBytesShort(bytes);
}

onMounted(async () => {
  try {
    usageRes.value = await systemStore.getUsageDbStatus();
  } catch (e) {
    usageRes.value = { ok: false, message: e instanceof Error ? e.message : String(e) };
  }
});

async function runAttachmentCleanup(dryRun: boolean): Promise<void> {
  attachmentLoading.value = true;
  try {
    const res = await systemStore.cleanupImageAttachments(attachmentDays.value, dryRun);
    if (!res.ok) {
      message.error(res.message || '附件清理失败');
      return;
    }
    report.value = parseAttachmentReport(res.data);
    message.success(res.message || (dryRun ? '统计完成' : '清理完成'));
  } finally {
    attachmentLoading.value = false;
  }
}

async function handleStats(): Promise<void> {
  await runAttachmentCleanup(true);
}

function handlePurgeClick(): void {
  const current = report.value;
  if (!current) return;
  dialog.warning({
    title: '确认清理超期附件',
    content: `将删除 ${current.expiredFiles} 个超期附件（${formatBytes(current.expiredBytes)}），此操作不可撤销。`,
    positiveText: '确认清理',
    negativeText: '取消',
    onPositiveClick: () => runAttachmentCleanup(false),
  });
}
</script>
