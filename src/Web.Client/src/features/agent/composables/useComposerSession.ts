import { watch } from 'vue';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import {
  cleanupObjectUrls,
  createEmptyDraft,
  genAttId,
  type ComposerAttachment,
  type ComposerDraft,
  type ComposerImageMime,
} from '@shared/utils/composerModel';

export interface SessionSavedDraft {
  text: string;
  images: Array<{ fileName: string; mime: ComposerImageMime }>;
}
export interface SessionDeps {
  readEditorToDraft(): void;
  plainTextOfDraft(): string;
  attachments(): ComposerAttachment[];
  replaceDraft(next: ComposerDraft): void;
  renderSession(text: string, atts: ComposerAttachment[]): void;
  setTextLen(n: number): void;
}
const savedDrafts: Record<string, SessionSavedDraft> = {};

export function useComposerSession(deps: SessionDeps) {
  const workspaceStore = useWorkspaceStore();
  function snapshotCurrent(): SessionSavedDraft {
    deps.readEditorToDraft();
    return { text: deps.plainTextOfDraft(), images: deps.attachments().map((a) => ({ fileName: a.fileName, mime: a.mime })) };
  }
  function restoreDraft(saved: SessionSavedDraft | undefined, current: ComposerDraft): ComposerDraft {
    cleanupObjectUrls(current);
    const next = createEmptyDraft();
    if (saved?.text) next.segments.push({ kind: 'text', text: saved.text });
    if (saved) {
      for (const meta of saved.images) {
        const attId = genAttId();
        let file: File;
        try {
          file = new File([], meta.fileName, { type: meta.mime });
        } catch {
          continue;
        }
        next.attachments[attId] = {
          attId,
          fileName: meta.fileName,
          mime: meta.mime,
          sizeBytes: 0,
          objectUrl: '',
          file,
          source: 'paste',
          status: 'invalid',
          error: '切换会话后需重新添加图片',
        };
        next.segments.push({ kind: 'image', attId });
      }
    }
    deps.replaceDraft(next);
    const atts = next.segments
      .filter((s) => s.kind === 'image')
      .map((s) => next.attachments[(s as { attId: string }).attId])
      .filter((a): a is ComposerAttachment => !!a);
    deps.renderSession(saved?.text || '', atts);
    deps.setTextLen(saved ? saved.text.length : 0);
    return next;
  }
  function restoreForSession(getCurrent: () => ComposerDraft): void {
    const sid = workspaceStore.activeSessionId || '';
    restoreDraft(sid ? savedDrafts[sid] : undefined, getCurrent());
  }
  function forgetSession(sid: string): void {
    if (sid) delete savedDrafts[sid];
  }
  function bindWatch(getCurrent: () => ComposerDraft): void {
    watch(
      () => workspaceStore.activeSessionId,
      (newSid, oldSid) => {
        if (oldSid) savedDrafts[oldSid] = snapshotCurrent();
        restoreDraft(newSid ? savedDrafts[newSid] : undefined, getCurrent());
      }
    );
  }
  return { snapshotCurrent, restoreDraft, restoreForSession, forgetSession, bindWatch };
}
export type ComposerSession = ReturnType<typeof useComposerSession>;
