<template>
  <div class="shrink-0 px-6 pb-4 pt-2" data-testid="prompt-composer">
    <div class="composer-wrap relative max-w-4xl mx-auto">
      <!-- need22-F03：斜杠候选面板（输入框上方） -->
      <SlashMenu
        v-if="!askMasked && !isGenerating && !subWaiting"
        class="slash-menu-host"
        :visible="slashVisible"
        :candidates="slashCandidates"
        :index="slashIndex"
        :error="slashError"
        :loading="slashLoading"
        @select="emit('slash-select', $event)"
        @hover="emit('slash-hover', $event)"
        @close="emit('slash-close')"
      />
      <div
        class="composer-box rounded-xl border border-subtle bg-surface focus-within:border-blue-500/80 focus-within:ring-2 focus-within:ring-blue-500/15 transition-all shadow-lg p-2.5 space-y-2"
        :class="dragOver ? 'drag-over' : ''"
        @dragover="emit('drag-over', $event)"
        @dragleave="emit('drag-leave')"
        @drop="emit('drop', $event)"
      >
        <div v-if="askMasked" class="rounded-md bg-elevated border border-subtle px-2.5 py-1.5 text-xs text-secondary">
          等待回答问题…（输入框已遮罩，提交后恢复）
        </div>
        <div
          v-if="subWaiting"
          class="rounded-md bg-elevated border border-subtle px-2.5 py-1.5 text-xs text-secondary"
          data-testid="composer-sub-waiting"
        >
          子代理执行中（{{ subDone }}/{{ subTotal }}），取消可掐断整树
          <button data-testid="composer-sub-open" class="text-accent hover:underline" @click="emit('open-sub')">
            查看子进度（{{ subDone }}/{{ subTotal }}）
          </button>
        </div>
        <!-- need02-P4（P4b#1）：发送中态（isGenerating 高亮停止按钮已有，此处补文案预期；输入框不置灰，草稿可继续编辑）。 -->
        <div
          v-if="isGenerating && !subWaiting"
          class="rounded-md bg-elevated border border-subtle px-2.5 py-1.5 text-xs text-secondary"
          data-testid="composer-sending"
        >
          发送中… Esc 停止
        </div>
        <div
          ref="editor"
          class="composer-editor"
          :contenteditable="!askMasked && !subWaiting"
          data-testid="composer-rich-input"
          aria-label="任务指令输入框，支持粘贴图片"
          :class="askMasked || subWaiting ? 'masked' : ''"
          :data-placeholder="subWaiting ? `子代理执行中（${subDone}/${subTotal}），取消可掐断整树` : undefined"
          @input="emit('editor-input')"
          @paste="emit('paste', $event)"
          @keydown="emit('keydown', $event)"
          @beforeinput="emit('before-input', $event)"
          @click="emit('editor-click', $event)"
        />
        <n-alert
          v-if="visionWarn"
          type="warning"
          :show-icon="false"
          size="small"
          class="composer-vision-warn"
          data-testid="composer-vision-warn"
        >
          当前模型不支持看图，发送时将自动忽略图片（文字仍发送）
        </n-alert>
        <div v-if="overLimitWarn" class="text-[11px] text-amber-500">图片超限，将只发送最新 {{ maxImages }} 张</div>
        <div v-if="oversizeWarn" class="text-[11px] text-amber-500">部分图片超过 10MB，发送时将自动跳过（文字仍发送）</div>
        <div class="flex items-center justify-between gap-2">
          <div class="flex items-center gap-2 min-w-0">
            <PresetPopover />
          </div>
          <div class="flex items-center justify-end gap-2">
            <n-button
              secondary
              size="small"
              data-testid="composer-add-image"
              :disabled="!canSend || isGenerating || askMasked || subWaiting"
              @click="emit('open-picker')"
            >
              🖼 添加图片
            </n-button>
            <input ref="picker" type="file" accept=".jpg,.jpeg,.png,.webp" multiple class="hidden" @change="emit('picker-change')" />
            <n-button
              v-if="!isGenerating"
              type="primary"
              size="small"
              data-testid="composer-send"
              :disabled="!canSend"
              @click="emit('send')"
            >
              <template #icon>
                <SendHorizontal class="w-3.5 h-3.5" />
              </template>
              发送 (Enter)
            </n-button>
            <n-button v-else type="error" size="small" @click="emit('stop')">
              <template #icon>
                <Square class="w-3.5 h-3.5" />
              </template>
              停止生成 (Esc)
            </n-button>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
<script setup lang="ts">
import { onMounted, useTemplateRef } from 'vue';
import { SendHorizontal, Square } from 'lucide-vue-next';
import PresetPopover from './PresetPopover.vue';
import SlashMenu from './SlashMenu.vue';
import type { SlashCandidate } from '@shared/utils/slashRegistry';

defineProps<{
  canSend: boolean;
  isGenerating: boolean;
  askMasked: boolean;
  subWaiting: boolean;
  subDone: number;
  subTotal: number;
  dragOver: boolean;
  visionWarn: boolean;
  overLimitWarn: boolean;
  oversizeWarn: boolean;
  maxImages: number;
  /** need22-F03：斜杠候选面板 */
  slashVisible: boolean;
  slashCandidates: SlashCandidate[];
  slashIndex: number;
  slashError: string;
  slashLoading: boolean;
}>();
const emit = defineEmits<{
  ready: [editor: HTMLElement | null, picker: HTMLInputElement | null];
  'editor-input': [];
  'editor-click': [e: MouseEvent];
  'before-input': [e: InputEvent];
  keydown: [e: KeyboardEvent];
  paste: [e: ClipboardEvent];
  drop: [e: DragEvent];
  'drag-over': [e: DragEvent];
  'drag-leave': [];
  'picker-change': [];
  'open-picker': [];
  send: [];
  stop: [];
  'open-sub': [];
  'slash-select': [candidate: SlashCandidate];
  'slash-hover': [index: number];
  'slash-close': [];
}>();
const editor = useTemplateRef<HTMLElement>('editor');
const picker = useTemplateRef<HTMLInputElement>('picker');
onMounted(() => {
  emit('ready', editor.value, picker.value);
});
</script>
<style>
.composer-editor {
  min-height: 52px;
  max-height: 220px;
  overflow-y: auto;
  outline: none;
  font-size: 12px;
  line-height: 1.6;
  color: var(--text-primary, #e5e7eb);
  word-break: break-word;
  white-space: pre-wrap;
}
.composer-editor:empty::before {
  content: '输入任务指令，Enter 发送，Shift+Enter 换行，可粘贴/拖入图片…';
  color: var(--text-secondary, #6b7280);
  pointer-events: none;
}
.composer-editor.masked {
  opacity: 0.6;
  pointer-events: none;
}
.composer-box.drag-over {
  border-color: rgba(59, 130, 246, 0.9);
  box-shadow: 0 0 0 2px rgba(59, 130, 246, 0.25);
}
.img-tag {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  border: 1px solid var(--border-subtle, #374151);
  border-radius: 4px;
  padding: 0 6px;
  font-size: 11px;
  line-height: 18px;
  background: var(--bg-elevated, #1f2937);
  vertical-align: middle;
  margin: 0 1px;
  user-select: none;
}
.img-tag.invalid {
  border-color: #f43f5e;
  background: rgba(244, 63, 94, 0.08);
}
.img-tag .fname {
  max-width: 120px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.img-tag .img-x {
  border: none;
  background: transparent;
  cursor: pointer;
  font-size: 12px;
  line-height: 1;
  padding: 0;
  color: inherit;
}
.img-tag .img-x:hover {
  color: #f43f5e;
}
.composer-vision-warn {
  font-size: 11px;
  line-height: 1.5;
}
.composer-vision-warn .n-alert-body {
  padding: 4px 8px !important;
}
.composer-vision-warn .n-alert-body__content {
  font-size: 11px !important;
  line-height: 1.5 !important;
}
</style>
