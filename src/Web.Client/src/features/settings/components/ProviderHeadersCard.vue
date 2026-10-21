<template>
  <n-card size="small" title="自定义请求头与预设变量引擎">
    <template #header-extra>
      <div class="flex items-center gap-2">
        <span class="text-xs text-secondary">启用自定义请求头</span>
        <n-switch
          :value="provider.enableCustomHeaders"
          size="small"
          data-testid="enable-custom-headers-switch"
          @update:value="onToggleHeaders($event)"
        />
      </div>
    </template>
    <div class="space-y-3">
      <div class="flex flex-wrap items-center gap-1.5">
        <span class="text-[11px] text-secondary mr-1">点击插入预设变量：</span>
        <n-tag
          v-for="v in presetVariables"
          :key="v.token"
          size="small"
          :bordered="false"
          type="info"
          class="cursor-pointer font-mono hover:opacity-80"
          :title="v.desc"
          @click="emit('insert-var', v.token)"
        >
          {{ v.token }}
        </n-tag>
      </div>
      <div v-if="provider.customHeaders.length > 0" class="space-y-2">
        <div v-for="(hdr, idx) in provider.customHeaders" :key="idx" class="grid grid-cols-1 sm:grid-cols-12 gap-2 items-center min-w-0">
          <div class="col-span-1 flex justify-center">
            <n-switch :value="hdr.enabled" size="small" @update:value="patchHeader(idx, (h) => (h.enabled = $event), true)" />
          </div>
          <div class="col-span-4 min-w-0">
            <n-input
              :value="hdr.key"
              placeholder="Header 名称，如 X-Session-Hash"
              size="small"
              class="font-mono text-xs w-full"
              @update:value="patchHeader(idx, (h) => (h.key = $event), false)"
              @blur="emit('preview')"
            />
          </div>
          <div class="col-span-5 min-w-0">
            <n-input
              :value="hdr.valueTemplate"
              placeholder="值或模板，如 ${session-hash}"
              size="small"
              class="font-mono text-xs w-full"
              @update:value="patchHeader(idx, (h) => (h.valueTemplate = $event), false)"
              @blur="emit('preview')"
            />
          </div>
          <div class="col-span-2 flex items-center justify-end gap-1 min-w-[86px]">
            <n-checkbox
              :checked="hdr.isSecret"
              size="small"
              title="日志脱敏"
              @update:checked="patchHeader(idx, (h) => (h.isSecret = $event), false)"
            >
              <span class="text-[11px] text-secondary">敏感</span>
            </n-checkbox>
            <n-button quaternary size="tiny" type="error" @click="emit('remove-header', idx)">
              <template #icon>
                <Trash2 class="w-3.5 h-3.5" />
              </template>
            </n-button>
          </div>
        </div>
      </div>
      <div v-else class="text-xs text-secondary py-1">
        暂无自定义请求头，可点击下方按钮添加（例如注入 <code class="font-mono">${session-hash}</code> 实现网关亲和路由；推荐同时保留
        <code class="font-mono">x-opencode-session</code> 以兼容 opencode 网关）。
      </div>
      <div class="flex items-center justify-between pt-1">
        <div class="flex flex-wrap items-center gap-2">
          <n-button size="tiny" secondary data-testid="add-custom-header-btn" @click="emit('add-header')">
            <template #icon>
              <Plus class="w-3.5 h-3.5" />
            </template>
            添加自定义 Header
          </n-button>
          <n-button size="tiny" secondary data-testid="add-opencode-session-header-btn" @click="emit('add-opencode-header')">
            + x-opencode-session（会话亲和）
          </n-button>
        </div>
        <span v-if="preview?.sessionHash" class="text-[11px] font-mono text-secondary" data-testid="preview-session-hash">
          当前会话 ${session-hash} 预览:
          <strong class="text-emerald-400">{{ preview.sessionHash }}</strong>
        </span>
      </div>
      <div
        v-if="provider.enableCustomHeaders && preview && Object.keys(preview.resolvedHeaders).length > 0"
        class="setting-inset p-2 text-[11px] font-mono space-y-1 min-w-0"
        data-testid="resolved-headers-preview-box"
      >
        <div class="text-secondary">实际发包 Header 注入预览：</div>
        <div v-for="(val, key) in preview.resolvedHeaders" :key="key" class="flex items-center justify-between gap-2 min-w-0">
          <span class="text-blue-400 shrink-0">{{ key }}:</span>
          <span class="text-emerald-400 min-w-0 break-all text-right">{{ val }}</span>
        </div>
      </div>
    </div>
  </n-card>
</template>
<script setup lang="ts">
import { Plus, Trash2 } from 'lucide-vue-next';
import type { ProviderConfig, CustomHeaderEntry, ProviderEndpointPreviewDto } from '@shared/bridge/hostBridge';
import { presetVariables } from '../composables/providerOptions';

const props = defineProps<{
  provider: ProviderConfig;
  preview: ProviderEndpointPreviewDto | null;
}>();
const emit = defineEmits<{
  change: [next: ProviderConfig];
  preview: [];
  'add-header': [];
  'add-opencode-header': [];
  'remove-header': [idx: number];
  'insert-var': [token: string];
}>();
function patch(fn: (p: ProviderConfig) => void, refresh: boolean): void {
  const next = JSON.parse(JSON.stringify(props.provider)) as ProviderConfig;
  fn(next);
  emit('change', next);
  if (refresh) emit('preview');
}
function patchHeader(idx: number, fn: (h: CustomHeaderEntry) => void, refresh: boolean): void {
  patch((p) => {
    const h = p.customHeaders[idx];
    if (h) fn(h);
  }, refresh);
}
function onToggleHeaders(v: boolean): void {
  patch((p) => {
    p.enableCustomHeaders = v;
  }, true);
}
</script>
