<!-- src/Web.Client/src/components/usage/FilterForm.vue -->
<!-- P16 用量筛选表单：预设条 + 日期范围 + Provider/Model + SessionId + 操作。 -->
<script setup lang="ts">
import { computed } from 'vue';
import HButton from '@/components/base/HButton.vue';
import HInput from '@/components/base/HInput.vue';
import HSelect from '@/components/base/HSelect.vue';
import { USAGE_PRESETS, type UsagePresetKey } from './usagePresets';
import type { ProviderDict } from './usageTypes';

 const props = defineProps<{
   fromInput: string;
   toInput: string;
   providerId: string;
   modelId: string;
   sessionId: string;
   providerDict: ProviderDict;
   activePreset: UsagePresetKey | null;
   querying: boolean;
   disabled?: boolean;
 }>();

const emit = defineEmits<{
  'update:fromInput': [string];
  'update:toInput': [string];
  'update:providerId': [string];
  'update:modelId': [string];
  'update:sessionId': [string];
  preset: [UsagePresetKey];
  submit: [];
  reset: [];
  export: [];
}>();

const providerIds = computed(() => Object.keys(props.providerDict).sort());
const modelOptions = computed(() => {
  if (!props.providerId) {
    // provider 为空：跨商全部模型（去重，表格显示 provider 列不合并）
    const seen = new Map<string, string>();
    for (const pid of providerIds.value) {
      for (const m of props.providerDict[pid]?.models ?? []) {
        if (!seen.has(m.id)) seen.set(m.id, m.display);
      }
    }
    return [...seen.entries()].map(([id, display]) => ({ id, display })).sort((a, b) => (a.id < b.id ? -1 : 1));
  }
  return (props.providerDict[props.providerId]?.models ?? []).slice().sort((a, b) => (a.id < b.id ? -1 : 1));
});

function onProvider(v: string): void {
  // 切换 provider 即清空 modelId（防跨商残留）
  emit('update:providerId', v);
  emit('update:modelId', '');
}
</script>

<template>
  <section aria-label="用量筛选">
    <div class="mb-2 flex flex-wrap gap-1.5" role="group" aria-label="时间预设">
       <HButton
         v-for="p in USAGE_PRESETS"
         :key="p.key"
         variant="ghost"
         size="sm"
         :title="p.title"
         :aria-pressed="activePreset === p.key"
         :class="activePreset === p.key ? 'bg-bg-muted text-fg' : ''"
         :disabled="disabled"
         @click="emit('preset', p.key)"
       >
         {{ p.label }}
       </HButton>
    </div>
    <div class="grid grid-cols-1 gap-2 md:grid-cols-2 xl:grid-cols-3">
       <label class="flex flex-col gap-1 text-xs text-fg-muted">
         开始时间
         <input
           type="datetime-local"
           class="h-8 rounded-md border-2 border-border-strong bg-bg-elevated px-3 text-sm text-fg disabled:opacity-60"
           :value="fromInput"
           :disabled="disabled"
           @input="emit('update:fromInput', ($event.target as HTMLInputElement).value)"
         />
       </label>
      <label class="flex flex-col gap-1 text-xs text-fg-muted">
        结束时间
         <input
           type="datetime-local"
           class="h-8 rounded-md border-2 border-border-strong bg-bg-elevated px-3 text-sm text-fg disabled:opacity-60"
           :value="toInput"
           :disabled="disabled"
           @input="emit('update:toInput', ($event.target as HTMLInputElement).value)"
         />
      </label>
      <label class="flex flex-col gap-1 text-xs text-fg-muted">
        服务商
         <HSelect :model-value="providerId" size="sm" :disabled="disabled" @update:model-value="onProvider">
          <option value="">全部服务商</option>
          <option v-for="pid in providerIds" :key="pid" :value="pid">
            {{ providerDict[pid]?.display || pid }}
          </option>
        </HSelect>
      </label>
      <label class="flex flex-col gap-1 text-xs text-fg-muted">
        模型
         <HSelect :model-value="modelId" size="sm" :disabled="disabled" @update:model-value="emit('update:modelId', $event)">
          <option value="">全部模型</option>
          <option v-for="m in modelOptions" :key="m.id" :value="m.id">{{ m.display }}</option>
        </HSelect>
      </label>
      <label class="flex flex-col gap-1 text-xs text-fg-muted">
        会话 ID（精确，可选）
         <HInput
           :model-value="sessionId"
            :disabled="disabled"
            size="sm"
          placeholder="sessionId，空即不过滤"
          clearable
          @update:model-value="emit('update:sessionId', String($event))"
        />
      </label>
      <div class="flex items-end gap-2">
        <HButton variant="primary" size="sm" :loading="querying" @click="emit('submit')">查询</HButton>
        <HButton variant="secondary" size="sm" @click="emit('reset')">重置</HButton>
        <HButton variant="ghost" size="sm" icon="download" @click="emit('export')">导出 CSV</HButton>
      </div>
    </div>
  </section>
</template>
