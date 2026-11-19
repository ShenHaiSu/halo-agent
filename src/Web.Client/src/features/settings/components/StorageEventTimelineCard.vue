<template>
  <n-card title="最近存储事件" size="small">
    <template #header-extra>
      <n-button size="tiny" secondary :loading="historyLoading" data-testid="load-storage-history-btn" @click="loadHistory">
        <template #icon>
          <History class="w-3.5 h-3.5" />
        </template>
        加载历史
      </n-button>
    </template>

    <n-timeline v-if="mergedEvents.length" size="small">
      <n-timeline-item
        v-for="ev in mergedEvents"
        :key="ev.key"
        :type="ev.timelineType"
        :title="ev.title"
        :content="ev.summary"
        :time="ev.time"
      />
    </n-timeline>
    <n-text v-else depth="3" data-testid="storage-events-empty">暂无事件（重启后清空）</n-text>
  </n-card>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue';
import { useMessage } from 'naive-ui';
import { History } from 'lucide-vue-next';
import { useSystemStore } from '@/stores/systemStore';
import { describeStorageEvent, eventDisplayMeta, formatEventTime } from '../composables/storageDisplay';

interface TimelineRow {
  key: string;
  type: string;
  summary: string;
  timestamp: number;
}

const systemStore = useSystemStore();
const message = useMessage();
const historyLoading = ref(false);
const persistedRows = ref<TimelineRow[]>([]);

const mergedEvents = computed(() => {
  const memoryRows: TimelineRow[] = systemStore.recentHostEvents.map((ev, index) => ({
    key: `memory-${ev.timestamp}-${ev.type}-${index}`,
    type: ev.type,
    summary: ev.summary,
    timestamp: ev.timestamp,
  }));
  const seen = new Set<string>();
  return [...memoryRows, ...persistedRows.value]
    .filter((row) => {
      const dedupeKey = `${row.timestamp}-${row.type}-${row.summary}`;
      if (seen.has(dedupeKey)) return false;
      seen.add(dedupeKey);
      return true;
    })
    .sort((a, b) => b.timestamp - a.timestamp)
    .map((row) => {
      const meta = eventDisplayMeta(row.type);
      return {
        key: row.key,
        timelineType: meta.timelineType,
        title: meta.title,
        summary: row.summary,
        time: formatEventTime(row.timestamp),
      };
    });
});

async function loadHistory(): Promise<void> {
  historyLoading.value = true;
  try {
    const res = await systemStore.getRecentHostEvents(20);
    if (!res.ok) {
      message.error(res.message || '加载历史事件失败');
      return;
    }
    persistedRows.value = (res.data ?? []).map((ev, index) => ({
      key: `disk-${ev.timestamp}-${ev.type}-${index}`,
      type: ev.type,
      summary: describeStorageEvent(ev.type, ev.payload),
      timestamp: Number(ev.timestamp) || 0,
    }));
    message.success(`已加载 ${persistedRows.value.length} 条历史事件`);
  } finally {
    historyLoading.value = false;
  }
}
</script>
