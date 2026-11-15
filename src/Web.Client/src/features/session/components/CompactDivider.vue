<template>
  <!-- P007：通栏分隔线，宽度跟随内容列，不受气泡限宽影响 -->
  <div class="compact-divider w-full select-none" data-testid="compact-divider" :data-stub-id="stubId">
    <div class="flex items-center gap-3 px-1 py-2">
      <span class="h-px flex-1 bg-subtle/70" aria-hidden="true" />
      <button
        type="button"
        class="inline-flex items-center gap-1.5 rounded-full border border-subtle bg-elevated px-3 py-1 text-secondary hover:text-primary transition-colors"
        :aria-expanded="expanded"
        data-testid="compact-pill"
        @click="emit('toggle')"
      >
        <Layers class="w-3.5 h-3.5" aria-hidden="true" />
        <span class="font-medium text-xs">{{ label }}</span>
        <ChevronDown v-if="!expanded" class="w-3 h-3" aria-hidden="true" />
        <ChevronUp v-else class="w-3 h-3" aria-hidden="true" />
      </button>
      <span class="h-px flex-1 bg-subtle/70" aria-hidden="true" />
    </div>
    <div
      v-if="expanded && summary"
      class="mt-1 rounded-lg border border-subtle bg-surface/40 px-3 py-2 text-secondary markdown-body min-w-0 text-sm leading-relaxed break-words"
      data-testid="compact-summary"
    >
      <div v-html="summaryHtml" />
      <div v-if="backupFile" class="mt-2 text-xs opacity-70">备份：{{ backupFile }}</div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ChevronDown, ChevronUp, Layers } from 'lucide-vue-next';

/** P007：压缩桩横栏，纯展示，无 store 与 bridge 依赖。 */
const props = defineProps<{
  stubId: string;
  label: string;
  summary: string;
  summaryHtml: string;
  backupFile?: string | null;
  expanded: boolean;
}>();
const emit = defineEmits<{ toggle: [] }>();
void props;
</script>
