<template>
  <div class="lg:col-span-4 flex flex-col space-y-2.5 min-w-0">
    <div class="flex items-center justify-between px-1">
      <span class="text-xs font-semibold text-secondary uppercase tracking-wider"> 已配置服务商 ({{ providers.length }}) </span>
      <n-button type="primary" size="tiny" secondary data-testid="add-provider-btn" @click="emit('add')">
        <template #icon>
          <Plus class="w-3.5 h-3.5" />
        </template>
        新增服务商
      </n-button>
    </div>
    <div class="space-y-2 min-w-0">
      <div
        v-for="item in providers"
        :key="item.id"
        class="setting-option p-3 cursor-pointer min-w-0"
        :class="{ 'setting-option-active': selectedId === item.id }"
        :data-testid="`provider-list-item-${item.id}`"
        @click="emit('select', item.id)"
      >
        <div class="flex items-center justify-between">
          <div class="flex items-center gap-2 min-w-0">
            <Server class="w-4 h-4 text-blue-400 shrink-0" />
            <span class="text-xs font-semibold truncate">{{ item.name }}</span>
          </div>
          <div class="flex items-center gap-1 shrink-0">
            <n-tag v-if="activeProviderId === item.id" type="info" size="small" :bordered="false"> 当前激活 </n-tag>
            <n-tag :type="item.enabled ? 'success' : 'default'" size="small" :bordered="false">
              {{ item.enabled ? '启用' : '停用' }}
            </n-tag>
          </div>
        </div>
        <div class="mt-1.5 text-[11px] font-mono text-secondary truncate min-w-0">
          {{ item.baseUrl }}
        </div>
        <div class="mt-2 flex items-center justify-between text-[11px] text-secondary">
          <span>{{ item.models.length }} 个模型</span>
          <span class="font-mono">
            代理:
            {{ item.proxyMode === 'Inherit' ? '继承全局' : item.proxyMode === 'None' ? '强制直连' : '独立代理' }}
          </span>
        </div>
      </div>
    </div>
  </div>
</template>
<script setup lang="ts">
import { Plus, Server } from 'lucide-vue-next';
import type { ProviderConfig } from '@shared/bridge/hostBridge';

defineProps<{
  providers: ProviderConfig[];
  activeProviderId: string;
  selectedId: string;
}>();
const emit = defineEmits<{
  select: [id: string];
  add: [];
}>();
</script>
