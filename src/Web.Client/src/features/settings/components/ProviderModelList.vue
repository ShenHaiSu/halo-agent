<template>
  <n-card size="small" title="模型列表与协议预算配置 (Models)">
    <template #header-extra>
      <n-button size="tiny" type="primary" secondary data-testid="add-model-btn" @click="emit('add-model')">
        <template #icon>
          <Plus class="w-3.5 h-3.5" />
        </template>
        添加模型
      </n-button>
    </template>
    <div class="space-y-2.5">
      <div
        v-for="(mdl, mIdx) in models"
        :key="mdl.id + '-' + mIdx"
        class="setting-option p-2.5 cursor-pointer min-w-0"
        :class="{ 'setting-option-active': expandedIndex === mIdx }"
        :data-testid="`model-row-${mIdx}`"
        @click="emit('toggle', mIdx)"
      >
        <div class="flex items-center justify-between gap-2 min-w-0">
          <div class="flex items-center gap-2 min-w-0">
            <ChevronDown class="w-3.5 h-3.5 transition-transform shrink-0" :class="{ 'rotate-180': expandedIndex === mIdx }" />
            <span class="text-xs font-semibold font-mono truncate">{{ mdl.id || '(未命名模型)' }}</span>
            <span v-if="mdl.displayName" class="text-[11px] text-secondary truncate">({{ mdl.displayName }})</span>
            <n-tag :type="mdl.protocol === 'resp' ? 'info' : 'success'" size="small" :bordered="false" class="font-mono shrink-0">{{
              mdl.protocol === 'resp' ? 'resp' : 'chat'
            }}</n-tag>
          </div>
          <div class="flex items-center gap-2 shrink-0" @click.stop>
            <span class="text-[11px] font-mono text-secondary hidden sm:inline"
              >{{ formatK(mdl.maxContextTokens) }} / Out {{ formatK(mdl.maxOutputTokens) }}</span
            >
            <n-tag v-if="isActive(providerId, mdl.id)" type="primary" size="small" :bordered="false">默认</n-tag>
            <n-button size="tiny" secondary @click="emit('set-active', mdl.id)">设为默认</n-button>
          </div>
        </div>
        <n-collapse-transition :show="expandedIndex === mIdx">
          <div class="pt-2.5 mt-2 border-t border-subtle/60 space-y-2.5" @click.stop>
            <div class="grid grid-cols-12 gap-2 min-w-0">
              <div class="col-span-12 sm:col-span-6 space-y-1 min-w-0">
                <label class="text-[11px] text-secondary">模型 ID (必填)</label>
                <n-input
                  :value="mdl.id"
                  placeholder="如 gpt-4o / o3-mini"
                  size="small"
                  class="font-mono text-xs"
                  @update:value="patchModel(mIdx, (m) => (m.id = $event))"
                />
              </div>
              <div class="col-span-12 sm:col-span-6 space-y-1 min-w-0">
                <label class="text-[11px] text-secondary">显示名称</label>
                <n-input
                  :value="mdl.displayName"
                  placeholder="显示别名（可选）"
                  size="small"
                  class="text-xs"
                  @update:value="patchModel(mIdx, (m) => (m.displayName = $event))"
                />
              </div>
              <div class="col-span-8 sm:col-span-4 space-y-1 min-w-0">
                <label class="text-[11px] text-secondary">通信协议</label>
                <n-select
                  :value="mdl.protocol"
                  :options="protocolOptions"
                  size="small"
                  class="font-mono text-xs"
                  @update:value="patchModel(mIdx, (m) => (m.protocol = $event))"
                />
              </div>
              <div class="col-span-4 sm:col-span-2 space-y-1 min-w-0">
                <label class="text-[11px] text-secondary">启用</label>
                <div class="flex items-center gap-1.5 h-[34px]">
                  <n-switch :value="mdl.enabled" size="small" @update:value="patchModel(mIdx, (m) => (m.enabled = $event))" />
                  <span class="text-[11px] text-secondary">{{ mdl.enabled ? '启用' : '停用' }}</span>
                </div>
              </div>
              <div class="col-span-12 sm:col-span-6 space-y-1 min-w-0">
                <label class="text-[11px] text-secondary">温度 (部分推理模型需留空)</label>
                <div class="flex items-center gap-2">
                  <n-input-number
                    :value="mdl.temperature"
                    :min="0"
                    :max="2"
                    :step="0.1"
                    size="small"
                    class="font-mono text-xs w-full max-w-full"
                    placeholder="留空则不下发"
                    @update:value="patchModel(mIdx, (m) => (m.temperature = $event))"
                  />
                  <n-button size="tiny" quaternary @click="emit('clear-temp', mIdx)">清空（留空）</n-button>
                </div>
              </div>
              <div class="col-span-12 sm:col-span-6 space-y-1 min-w-0">
                <label class="text-[11px] text-secondary">最大上下文 Token</label>
                <n-input-number
                  :value="mdl.maxContextTokens"
                  :min="1024"
                  :max="2000000"
                  :step="4096"
                  size="small"
                  class="font-mono text-xs w-full max-w-full"
                  @update:value="patchModel(mIdx, (m) => (m.maxContextTokens = $event ?? 128000))"
                />
                <div class="flex flex-wrap items-center gap-1.5 pt-1">
                  <n-button
                    v-for="p in CONTEXT_PRESETS"
                    :key="p.value"
                    size="tiny"
                    secondary
                    :type="mdl.maxContextTokens === p.value ? 'primary' : 'default'"
                    @click="emit('preset', mIdx, 'maxContextTokens', p.value)"
                    >{{ p.label }}</n-button
                  >
                </div>
              </div>
              <div class="col-span-12 sm:col-span-6 space-y-1 min-w-0">
                <label class="text-[11px] text-secondary">最大输出 Token</label>
                <n-input-number
                  :value="mdl.maxOutputTokens"
                  :min="256"
                  :max="256000"
                  :step="1024"
                  size="small"
                  class="font-mono text-xs w-full max-w-full"
                  @update:value="patchModel(mIdx, (m) => (m.maxOutputTokens = $event ?? 4096))"
                />
                <div class="flex flex-wrap items-center gap-1.5 pt-1">
                  <n-button
                    v-for="p in OUTPUT_PRESETS"
                    :key="p.value"
                    size="tiny"
                    secondary
                    :type="mdl.maxOutputTokens === p.value ? 'primary' : 'default'"
                    @click="emit('preset', mIdx, 'maxOutputTokens', p.value)"
                    >{{ p.label }}</n-button
                  >
                </div>
              </div>
              <div class="col-span-12 space-y-1.5 min-w-0 rounded-md border border-subtle/60 p-2">
                <div class="flex flex-wrap items-center gap-x-4 gap-y-2">
                  <div class="flex items-center gap-2">
                    <span class="text-[11px] text-secondary">可读图片（多模态）</span>
                    <n-switch
                      :value="visionOf(mdl).supportsVision === true"
                      size="small"
                      :data-testid="`model-supports-vision-switch-${mIdx}`"
                      @update:value="emit('vision', mIdx, 'supportsVision', $event)"
                    />
                  </div>
                  <template v-if="visionOf(mdl).supportsVision === true">
                    <div class="flex items-center gap-2">
                      <span class="text-[11px] text-secondary">每轮最多图片（0=默认4，上限16）</span>
                      <n-input-number
                        :value="visionOf(mdl).maxImagesPerTurn ?? 4"
                        :min="0"
                        :max="16"
                        :step="1"
                        size="small"
                        class="font-mono text-xs w-28"
                        :data-testid="`model-max-images-input-${mIdx}`"
                        @update:value="emit('vision', mIdx, 'maxImagesPerTurn', $event)"
                      />
                    </div>
                    <div class="flex items-center gap-2">
                      <span class="text-[11px] text-secondary">清晰度</span>
                      <n-select
                        :value="visionOf(mdl).visionDetail ?? 'high'"
                        :options="visionDetailOptions"
                        size="small"
                        class="w-28"
                        :data-testid="`model-vision-detail-select-${mIdx}`"
                        @update:value="emit('vision', mIdx, 'visionDetail', $event)"
                      />
                    </div>
                  </template>
                </div>
                <div class="text-[11px] text-secondary">关闭后发送图片将被拦截；图片按 jpg/png/webp ≤10MB 校验，超限自动取最新 N 张。</div>
              </div>
            </div>
            <div class="col-span-12 flex flex-wrap items-center gap-x-4 gap-y-1 rounded-md border border-subtle/60 p-2">
              <div class="flex items-center gap-2">
                <span class="text-[11px] text-secondary">历史携带思考链</span>
                <n-switch
                  :value="visionOf(mdl).includeReasoningInHistory !== false"
                  size="small"
                  :data-testid="`model-reasoning-switch-${mIdx}`"
                  @update:value="emit('vision', mIdx, 'includeReasoningInHistory', $event)"
                />
              </div>
              <span class="text-[11px] text-secondary">关闭后历史 reasoning 不发包（严格网关 400 时可关）</span>
            </div>
            <div class="text-[11px] font-mono text-secondary break-all min-w-0">
              {{ mdl.protocol === 'resp' ? 'resp → ' : 'chat → ' }}{{ mdl.protocol === 'resp' ? respEndpoint : chatEndpoint }}
            </div>
            <div class="flex flex-wrap items-center justify-between gap-2 pt-1">
              <n-button
                size="tiny"
                :type="isActive(providerId, mdl.id) ? 'primary' : 'default'"
                secondary
                @click="emit('set-active', mdl.id)"
              >
                <template #icon>
                  <CheckCircle2 class="w-3.5 h-3.5" />
                </template>
                {{ isActive(providerId, mdl.id) ? '当前默认模型' : '设为默认模型' }}
              </n-button>
              <n-button quaternary size="tiny" type="error" :disabled="models.length <= 1" @click="emit('remove-model', mIdx)">
                <template #icon>
                  <Trash2 class="w-3.5 h-3.5" />
                </template>
                删除模型
              </n-button>
            </div>
          </div>
        </n-collapse-transition>
      </div>
    </div>
  </n-card>
</template>
<script setup lang="ts">
import { Plus, Trash2, CheckCircle2, ChevronDown } from 'lucide-vue-next';
import type { ModelConfig } from '@shared/bridge/hostBridge';
import {
  CONTEXT_PRESETS,
  OUTPUT_PRESETS,
  formatK,
  protocolOptions,
  visionDetailOptions,
  visionOf,
  type VisionField,
} from '../composables/providerOptions';

const props = defineProps<{
  models: ModelConfig[];
  providerId: string;
  activeProviderId: string;
  activeModelId: string;
  expandedIndex: number | null;
  chatEndpoint: string;
  respEndpoint: string;
}>();
const emit = defineEmits<{
  toggle: [idx: number];
  'add-model': [];
  'remove-model': [idx: number];
  'set-active': [modelId: string];
  preset: [idx: number, field: 'maxContextTokens' | 'maxOutputTokens', value: number];
  'clear-temp': [idx: number];
  vision: [idx: number, field: VisionField, value: unknown];
  'change-models': [models: ModelConfig[]];
}>();
function isActive(providerId: string, modelId: string): boolean {
  return props.activeProviderId === providerId && props.activeModelId === modelId;
}
function patchModel(idx: number, fn: (m: ModelConfig) => void): void {
  const next = props.models.map((m, i) => {
    const c = JSON.parse(JSON.stringify(m)) as ModelConfig;
    if (i === idx) fn(c);
    return c;
  });
  emit('change-models', next);
}
</script>
