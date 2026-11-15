<template>
  <div class="p-2.5 space-y-1.5">
    <div v-if="card.status === 'running'" class="font-mono app-text-small text-secondary break-all whitespace-pre-wrap">
      正在读取图片：{{ viewImagePathShort }}
    </div>
    <div v-else-if="card.status === 'error'" class="font-mono app-text-small text-rose-400 break-all whitespace-pre-wrap select-text">
      {{ displayOutput || '读取图片失败' }}
    </div>
    <div v-else class="space-y-1.5">
      <div class="font-mono app-text-small text-emerald-400 break-all">
        已读取 {{ viewImageName }} ({{ viewImageWH }}, {{ viewImageKB }})
      </div>
      <div v-if="viewImages.length" class="flex flex-wrap gap-1.5">
        <template v-for="img in viewImages" :key="img.imageId">
          <n-image
            v-if="toolThumbOf(img)"
            :src="toolThumbOf(img)"
            :alt="img.fileName"
            width="96"
            object-fit="cover"
            class="rounded border border-subtle"
            :data-testid="`tool-image-${img.imageId}`"
          />
          <n-tag v-else size="small" :bordered="true" :title="img.fileName">
            {{ img.fileName }}
          </n-tag>
        </template>
      </div>
      <div class="flex items-center gap-2">
        <n-button size="tiny" quaternary :data-testid="`tool-open-image-${card.callId}`" @click="handleOpenOrigin"> 打开原图 </n-button>
        <n-button size="tiny" quaternary @click="handleCopy">
          <template #icon><Copy class="w-3 h-3" /></template>
          复制输出
        </n-button>
      </div>
      <div
        v-if="displayOutput"
        class="max-h-64 overflow-y-auto bg-[var(--tool-output-bg)] p-2.5 rounded font-mono app-text-small leading-relaxed text-[var(--tool-output-text)] whitespace-pre-wrap break-all select-text border border-subtle"
      >
        {{ viewImageTruncated }}
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { Copy } from 'lucide-vue-next';
import type { AgentToolCard } from '@features/agent/stores/model';
import type { ChatImageRef } from '@shared/bridge/hostBridge';
import { formatSmartBytesShort } from '@shared/utils/formatSmartBytes';
import { parseToolArgs, stringArg } from '@features/agent/utils/toolArgs';
import { useImageThumb } from '@features/agent/composables/useImageThumb';
import { useToolCardActions } from '@features/agent/composables/useToolCardActions';

/** 读图分支三态：读取中、失败、完成（缩略图行 + 原图入口 + 文本截断）。 */
const props = defineProps<{ card: AgentToolCard }>();

const actions = useToolCardActions(() => props.card);
const thumbs = useImageThumb();

const displayOutput = computed(() => actions.displayOutput.value);
const viewImageTruncated = computed(() => {
  const out = displayOutput.value;
  if (out.length > 6000) return out.slice(0, 6000) + '\n...（输出过长已截断）';
  return out;
});

const viewImages = computed<ChatImageRef[]>(() => (Array.isArray(props.card.images) ? props.card.images : []));

const viewImageRawPath = computed(() => stringArg(parseToolArgs(props.card), 'path', 'file_path'));

const viewImagePathShort = computed(() => {
  const p = viewImageRawPath.value || '(未知路径)';
  return p.length > 60 ? '…' + p.slice(p.length - 60) : p;
});

const viewImageName = computed(() => {
  const imgs = viewImages.value;
  if (imgs.length > 0 && imgs[0].fileName) return imgs[0].fileName;
  const p = viewImageRawPath.value;
  if (!p) return '(未知图片)';
  const parts = p.split(/[\\/]/).filter(Boolean);
  return parts.length > 0 ? parts[parts.length - 1] : p;
});

const viewImageWH = computed(() => {
  const first = viewImages.value.length > 0 ? viewImages.value[0] : null;
  if (first && first.width > 0 && first.height > 0) return `${first.width}x${first.height}`;
  return '?x?';
});

const viewImageKB = computed(() => {
  const first = viewImages.value.length > 0 ? viewImages.value[0] : null;
  if (first && first.sizeBytes > 0) return formatSmartBytesShort(first.sizeBytes);
  return '?KB';
});

function toolThumbOf(img: ChatImageRef): string {
  return thumbs.thumbOf(img);
}

function handleOpenOrigin(): void {
  const first = viewImages.value.length > 0 ? viewImages.value[0] : null;
  const target = (first && first.relPath) || viewImageRawPath.value;
  void actions.handleOpenOrigin(target);
}

function handleCopy(): void {
  void actions.handleCopyText(displayOutput.value);
}
</script>
