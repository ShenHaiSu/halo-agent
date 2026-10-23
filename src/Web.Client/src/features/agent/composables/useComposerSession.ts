import { watch } from 'vue';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import {
  cleanupObjectUrls,
  createEmptyDraft,
  deserialize,
  genAttId,
  type ComposerAttachment,
  type ComposerDraft,
  type ComposerImageMime,
  type ComposerSegment,
} from '@shared/utils/composerModel';

export interface SessionSavedDraft {
  text: string;
  images: Array<{ fileName: string; mime: ComposerImageMime }>;
  /** need22-F02：草稿箱里的 skill（scope/name），恢复时还原为 skill 段 */
  skills: Array<{ scope: 'global' | 'project'; name: string }>;
}
export interface SessionDeps {
  readEditorToDraft(): void;
  plainTextOfDraft(): string;
  attachments(): ComposerAttachment[];
  /** need22-F02：当前草稿里的 skill 段 */
  skills(): Array<{ scope: 'global' | 'project'; name: string }>;
  replaceDraft(next: ComposerDraft): void;
  renderSession(segments: ComposerSegment[], attachments: Record<string, ComposerAttachment>): void;
  setTextLen(n: number): void;
}
const savedDrafts: Record<string, SessionSavedDraft> = {};

export function useComposerSession(deps: SessionDeps) {
  const workspaceStore = useWorkspaceStore();
  function snapshotCurrent(): SessionSavedDraft {
    deps.readEditorToDraft();
    return {
      text: deps.plainTextOfDraft(),
      images: deps.attachments().map((a) => ({ fileName: a.fileName, mime: a.mime })),
      skills: deps.skills(),
    };
  }
  function restoreDraft(saved: SessionSavedDraft | undefined, current: ComposerDraft): ComposerDraft {
    cleanupObjectUrls(current);
    const next = createEmptyDraft();
    // need22-F02：文本里含 skill marker ⇒ 直接还原成 skill 段（保持原位置）
    const parsed = deserialize(saved?.text || '', {});
    next.segments.push(...parsed.segments);
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
      // 草稿箱只存 scope/name（接受"skill 追加到末尾"的限制）；文本里已有同名则不重复
      for (const s of saved.skills || []) {
        const exists = next.segments.some((seg) => seg.kind === 'skill' && seg.scope === s.scope && seg.name === s.name);
        if (exists) continue;
        next.segments.push({ kind: 'skill', scope: s.scope, name: s.name });
      }
    }
    deps.replaceDraft(next);
    deps.renderSession(next.segments, next.attachments);
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
