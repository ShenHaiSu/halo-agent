<template>
  <n-modal :show="show" preset="card" style="width: 720px" title="拉取供应商模型" data-testid="catalog-dialog" @close="emit('close')">
    <div class="space-y-3">
      <div class="flex items-start justify-between gap-2">
        <div class="min-w-0 text-xs text-secondary">
          <div class="break-all">
            <span class="font-semibold text-primary">{{ providerName }}</span>
            <span v-if="store.snapshot"> · {{ store.snapshot.modelsUrl }}</span>
          </div>
          <div v-if="store.snapshot" class="mt-0.5 font-mono text-[11px]">
            代理 {{ store.snapshot.effectiveProxySummary }} · 用时 {{ store.snapshot.elapsedMs }} ms
          </div>
        </div>
        <n-button v-if="store.snapshot" size="tiny" secondary data-testid="catalog-copy-url" @click="copyModelsUrl">
          <template #icon>
            <Copy class="w-3.5 h-3.5" />
          </template>
          复制 modelsUrl
        </n-button>
      </div>
      <div v-if="store.snapshot" class="text-[11px] font-mono text-secondary">
        共 {{ store.counts.total }} / 新增 {{ store.counts.new }} / 已存在 {{ store.counts.existing }} / 已忽视 {{ store.counts.ignored }}
      </div>
      <div class="flex items-center gap-2">
        <n-input v-model:value="store.search" clearable size="small" placeholder="搜索模型 id / 归属" data-testid="catalog-search">
          <template #prefix>
            <Search class="w-3.5 h-3.5" />
          </template>
        </n-input>
        <div class="flex items-center gap-1.5 shrink-0">
          <span class="text-[11px] text-secondary">已忽视</span>
          <n-switch :value="store.showIgnored" size="small" @update:value="setShowIgnored" />
        </div>
        <n-button size="tiny" secondary @click="selectAllFiltered">全选本页</n-button>
        <n-button size="tiny" secondary @click="clear">清空</n-button>
      </div>
      <div v-if="store.loading" class="text-xs text-secondary">正在拉取供应商模型…</div>
      <div v-else-if="store.lastError" class="text-xs">
        <span class="text-rose-400">{{ store.lastError.message || '拉取失败' }}</span>
        <span v-if="store.lastError.code" class="ml-2 font-mono text-secondary">{{ store.lastError.code }}</span>
        <div class="mt-1 flex items-center gap-2">
          <n-button size="tiny" secondary @click="retry">重试</n-button>
          <n-button v-if="store.lastError.code === 'CATALOG_UPSTREAM_401_403'" size="tiny" secondary @click="emit('goto-headers')">
            去改自定义头
          </n-button>
          <n-button v-if="store.lastError.code === 'CATALOG_UPSTREAM_404'" size="tiny" secondary @click="emit('goto-baseurl')">
            去改 BaseURL
          </n-button>
        </div>
      </div>
      <div v-else-if="!store.filtered.length" class="text-xs text-secondary">
        <n-empty size="small" description="暂无模型：可重试拉取或检查 BaseURL" />
      </div>
      <div v-else class="max-h-[320px] space-y-1.5 overflow-auto pr-1">
        <div
          v-for="m in store.filtered"
          :key="m.id"
          class="setting-option flex items-center gap-2 p-2"
          :data-testid="`catalog-row-${m.id}`"
        >
          <n-checkbox :checked="store.selection.has(m.id)" :disabled="m.state !== 'new'" @update:checked="toggle(m.id)" />
          <span class="min-w-0 flex-1 truncate font-mono text-xs">{{ m.id }}</span>
          <span v-if="m.ownedBy" class="max-w-[140px] truncate text-[11px] text-secondary">{{ m.ownedBy }}</span>
          <n-tag v-if="m.state === 'new'" type="info" size="small" :bordered="false">新增</n-tag>
          <n-tag v-else-if="m.state === 'existing'" type="success" size="small" :bordered="false">
            已存在{{ m.existsEnabled ? '·已启用' : '·已停用' }}
          </n-tag>
          <n-tag v-else type="default" size="small" :bordered="false">已忽视</n-tag>
          <n-button
            v-if="m.state === 'new' || m.state === 'ignored'"
            size="tiny"
            quaternary
            :loading="store.ignoring"
            @click="toggleIgnored(m.id, m.state)"
          >
            {{ m.state === 'ignored' ? '取消忽视' : '忽视' }}
          </n-button>
          <n-button v-if="m.state === 'existing'" size="tiny" quaternary @click="emit('set-default', m.id)"> 设为默认 </n-button>
        </div>
      </div>
      <div class="flex items-center justify-end gap-2 pt-1">
        <n-button
          size="small"
          secondary
          type="primary"
          :disabled="!store.canImport"
          :loading="store.importing"
          data-testid="catalog-import-enabled"
          @click="doImport(false)"
        >
          引入并启用选中({{ store.counts.selected }})
        </n-button>
        <n-button
          size="small"
          secondary
          :disabled="!store.canImport"
          :loading="store.importing"
          data-testid="catalog-import-disabled"
          @click="doImport(true)"
        >
          引入但不启用选中({{ store.counts.selected }})
        </n-button>
        <n-button size="small" data-testid="catalog-close" @click="emit('close')">关闭</n-button>
      </div>
    </div>
  </n-modal>
</template>
<script setup lang="ts">
import { Copy, Search } from 'lucide-vue-next';
import { useMessage } from 'naive-ui';
import { useModelCatalogStore } from '@/stores/modelCatalogStore';

defineProps<{
  show: boolean;
  providerName: string;
}>();
const emit = defineEmits<{
  close: [];
  imported: [];
  'goto-headers': [];
  'goto-baseurl': [];
  'set-default': [modelId: string];
}>();

const store = useModelCatalogStore();
const message = useMessage();

function toggle(id: string): void {
  store.toggle(id);
}
function selectAllFiltered(): void {
  store.selectAllFiltered();
}
function clear(): void {
  store.clear();
}
function setShowIgnored(v: boolean): void {
  store.setShowIgnored(v);
}
async function retry(): Promise<void> {
  if (store.providerId) await store.fetch(store.providerId, store.draftModels);
}
async function doImport(importDisabled: boolean): Promise<void> {
  const res = await store.importSelected(importDisabled);
  if (res.ok) {
    message.success(importDisabled ? '已引入但不启用' : '已引入并启用');
    emit('imported');
  } else {
    message.error(res.message || '引入失败');
  }
}
async function toggleIgnored(id: string, state: string): Promise<void> {
  const res = await store.setIgnored([id], state !== 'ignored');
  if (!res.ok) message.error(res.message || '忽视操作失败');
}
async function copyModelsUrl(): Promise<void> {
  const url = store.snapshot?.modelsUrl || '';
  if (!url) return;
  try {
    await navigator.clipboard.writeText(url);
    message.success('已复制 Models 端点');
  } catch {
    message.warning(url);
  }
}
</script>
