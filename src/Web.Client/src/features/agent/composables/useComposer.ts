import { computed, ref, watch, type ComputedRef, type Ref } from 'vue';
import { useMessage } from 'naive-ui';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useConfigStore } from '@/stores/configStore';
import { useAgentStream } from '@features/agent/stores/useAgentStream';
import {
  createEmptyDraft,
  cleanupObjectUrls,
  insertImage,
  isEmpty,
  type ComposerAttachment,
  type ComposerDraft,
} from '@shared/utils/composerModel';
import {
  backspacePill,
  closestRemoveId,
  insertPill,
  insertTextFallback,
  readSegments,
  renderDraft,
  renderText,
  textLenOf,
} from './composerEditorDom';
import { useComposerAttachments } from './useComposerAttachments';
import { useComposerSession } from './useComposerSession';
export type { SessionSavedDraft as ComposerSavedDraft } from './useComposerSession';
export interface SendHandlers {
  onSend: () => void;
  onStop: () => void;
}
export interface ComposerApi {
  askMasked: Ref<boolean>;
  dragOver: Ref<boolean>;
  textLen: Ref<number>;
  attachmentList: ComputedRef<ComposerAttachment[]>;
  readyCount: ComputedRef<number>;
  maxImages: ComputedRef<number>;
  supportsVision: ComputedRef<boolean>;
  visionWarn: ComputedRef<boolean>;
  overLimitWarn: ComputedRef<boolean>;
  oversizeWarn: ComputedRef<boolean>;
  isGenerating: ComputedRef<boolean>;
  bindSend(h: SendHandlers): void;
  getDraft(): ComposerDraft;
  readEditorToDraft(): void;
  plainTextOfDraft(): string;
  clearDraft(): void;
  insertTextAtCaret(text: string): void;
  focusInput(): void;
  setAskMasked(v: boolean): void;
  restoreForSession(): void;
  isEmptyDraft(): boolean;
  onEditorInput(): void;
  onEditorClick(e: MouseEvent): void;
  onBeforeInput(e: InputEvent): void;
  onKeydown(e: KeyboardEvent): void;
  onPaste(e: ClipboardEvent): void;
  onDrop(e: DragEvent): void;
  onDragOver(e: DragEvent): void;
  onDragLeave(): void;
  openPicker(): void;
  onPickerChange(): void;
}

export function useComposer(editorRef: Ref<HTMLElement | null>, fileRef: Ref<HTMLInputElement | null>): ComposerApi {
  const message = useMessage();
  const workspaceStore = useWorkspaceStore();
  const configStore = useConfigStore();
  const stream = useAgentStream();
  const handlers: SendHandlers = { onSend: () => undefined, onStop: () => undefined };
  let draft: ComposerDraft = createEmptyDraft();
  const draftVer = ref(0);
  const askMasked = ref(false);
  const dragOver = ref(false);
  const textLen = ref(0);
  const setDraft = (next: ComposerDraft): void => {
    draft = next;
    draftVer.value++;
  };
  const getDraft = (): ComposerDraft => draft;
  const isGenerating = computed(() => stream.isGenerating);
  const attachmentList = computed<ComposerAttachment[]>(() => {
    void draftVer.value;
    return Object.values(draft.attachments);
  });
  const readyCount = computed(() => attachmentList.value.filter((a) => a.status === 'ready').length);
  const maxImages = computed(() => {
    const raw = Number(configStore.activeModel?.maxImagesPerTurn);
    if (!Number.isFinite(raw) || raw === 0) return 4;
    return Math.max(1, Math.min(16, Math.trunc(raw)));
  });
  const supportsVision = computed(() => configStore.activeModel?.supportsVision === true);
  const visionWarn = computed(() => readyCount.value > 0 && !supportsVision.value);
  const overLimitWarn = computed(() => readyCount.value > maxImages.value);
  const oversizeWarn = computed(() => attachmentList.value.some((a) => a.status === 'oversize'));
  function gcUnreferenced(): void {
    const used = new Set<string>();
    for (const s of draft.segments) if (s.kind === 'image') used.add(s.attId);
    for (const id of Object.keys(draft.attachments)) {
      if (used.has(id)) continue;
      try {
        const u = draft.attachments[id].objectUrl;
        if (u) URL.revokeObjectURL(u);
      } catch {
        /* ignore */
      }
      delete draft.attachments[id];
    }
  }
  function readEditorToDraft(): void {
    const editor = editorRef.value;
    if (!editor) return;
    setDraft({ segments: readSegments(editor, (id) => !!draft.attachments[id]), attachments: { ...draft.attachments } });
    gcUnreferenced();
    textLen.value = textLenOf(draft.segments);
  }
  function renderEditorFromText(text: string): void {
    const editor = editorRef.value;
    if (!editor) return;
    renderText(editor, text);
    textLen.value = text.length;
  }
  function insertTextAtCaret(text: string): void {
    const editor = editorRef.value;
    if (!editor) return;
    editor.focus();
    insertTextFallback(editor, text);
    readEditorToDraft();
  }
  function focusInput(): void {
    try {
      editorRef.value?.focus();
    } catch {
      /* ignore */
    }
  }
  function insertPillAtCaret(att: ComposerAttachment): void {
    const editor = editorRef.value;
    if (!editor) {
      setDraft(insertImage(draft, att.attId));
      return;
    }
    editor.focus();
    insertPill(editor, att);
    readEditorToDraft();
  }
  const attachments = useComposerAttachments({
    editorRef,
    fileRef,
    isGenerating,
    askMasked,
    getDraft,
    setDraft,
    readEditorToDraft,
    insertPillAtCaret,
    insertTextAtCaret,
    warn: (m) => message.warning(m),
    error: (m) => message.error(m),
    setDragOver: (v) => (dragOver.value = v),
  });
  function onDragOver(e: DragEvent): void {
    e.preventDefault();
    if (!isGenerating.value && !askMasked.value) dragOver.value = true;
  }
  function onDragLeave(): void {
    dragOver.value = false;
  }
  function openPicker(): void {
    if (isGenerating.value || askMasked.value) return;
    fileRef.value?.click();
  }
  function onEditorInput(): void {
    readEditorToDraft();
  }
  function onEditorClick(e: MouseEvent): void {
    const attId = closestRemoveId(e.target);
    if (attId) {
      e.preventDefault();
      e.stopPropagation();
      attachments.removeImage(attId);
    }
  }
  function onBeforeInput(e: InputEvent): void {
    if (e.inputType !== 'deleteContentBackward' || !editorRef.value) return;
    const pill = backspacePill(editorRef.value);
    if (pill) {
      e.preventDefault();
      const attId = pill.getAttribute('data-att') || '';
      if (attId) attachments.removeImage(attId);
    }
  }
  function onKeydown(e: KeyboardEvent): void {
    if (e.key === 'Enter' && !e.shiftKey && !(e as unknown as { isComposing?: boolean }).isComposing) {
      e.preventDefault();
      handlers.onSend();
    } else if (e.key === 'Escape' && isGenerating.value) {
      e.preventDefault();
      handlers.onStop();
    }
  }
  function plainTextOfDraft(): string {
    return draft.segments
      .filter((s) => s.kind === 'text')
      .map((s) => (s as { kind: 'text'; text: string }).text)
      .join('');
  }
  function clearDraft(): void {
    cleanupObjectUrls(draft);
    setDraft(createEmptyDraft());
    renderEditorFromText('');
    session.forgetSession(workspaceStore.activeSessionId || '');
  }
  const session = useComposerSession({
    readEditorToDraft,
    plainTextOfDraft,
    attachments: () => attachmentList.value,
    replaceDraft: setDraft,
    renderSession: (text, atts) => {
      const editor = editorRef.value;
      if (editor) renderDraft(editor, text, atts);
    },
    setTextLen: (n) => (textLen.value = n),
  });
  session.bindWatch(getDraft);
  watch(askMasked, (v) => {
    if (editorRef.value) editorRef.value.contentEditable = v ? 'false' : 'true';
  });
  return {
    askMasked,
    dragOver,
    textLen,
    attachmentList,
    readyCount,
    maxImages,
    supportsVision,
    visionWarn,
    overLimitWarn,
    oversizeWarn,
    isGenerating,
    bindSend: (h: SendHandlers): void => {
      handlers.onSend = h.onSend;
      handlers.onStop = h.onStop;
    },
    getDraft,
    readEditorToDraft,
    plainTextOfDraft,
    clearDraft,
    insertTextAtCaret,
    focusInput,
    setAskMasked: (v: boolean): void => {
      askMasked.value = v;
    },
    restoreForSession: () => session.restoreForSession(getDraft),
    isEmptyDraft: () => isEmpty(draft),
    onEditorInput,
    onEditorClick,
    onBeforeInput,
    onKeydown,
    onPaste: attachments.onPaste,
    onDrop: attachments.onDrop,
    onDragOver,
    onDragLeave,
    openPicker,
    onPickerChange: attachments.onPickerChange,
  };
}
