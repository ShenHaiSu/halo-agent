<template>
  <PromptComposerView
    :can-send="send.canSend"
    :is-generating="c.isGenerating"
    :ask-masked="c.askMasked"
    :sub-waiting="send.subWaiting"
    :sub-done="send.subDone"
    :sub-total="send.subTotal"
    :drag-over="c.dragOver"
    :vision-warn="c.visionWarn"
    :over-limit-warn="c.overLimitWarn"
    :oversize-warn="c.oversizeWarn"
    :max-images="c.maxImages"
    @ready="onReady"
    @editor-input="c.onEditorInput()"
    @editor-click="c.onEditorClick($event)"
    @before-input="c.onBeforeInput($event)"
    @keydown="c.onKeydown($event)"
    @paste="c.onPaste($event)"
    @drop="c.onDrop($event)"
    @drag-over="c.onDragOver($event)"
    @drag-leave="c.onDragLeave()"
    @picker-change="c.onPickerChange()"
    @open-picker="c.openPicker()"
    @send="send.handleSend()"
    @stop="send.handleStop()"
    @open-sub="send.openFirstSub()"
  />
</template>
<script setup lang="ts">
import { reactive, ref } from 'vue';
import PromptComposerView from './PromptComposerView.vue';
import { useComposer } from '../composables/useComposer';
import { useSendTurn } from '../composables/useSendTurn';

const editorRef = ref<HTMLElement | null>(null);
const fileRef = ref<HTMLInputElement | null>(null);
const raw = useComposer(editorRef, fileRef);
const sendRaw = useSendTurn(raw);
const c = reactive(raw);
const send = reactive(sendRaw);
c.bindSend({ onSend: () => send.handleSend(), onStop: () => send.handleStop() });
function onReady(ed: HTMLElement | null, fi: HTMLInputElement | null): void {
  editorRef.value = ed;
  fileRef.value = fi;
}
</script>
