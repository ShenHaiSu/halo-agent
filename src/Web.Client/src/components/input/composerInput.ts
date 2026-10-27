// src/Web.Client/src/components/input/composerInput.ts
import type { Ref } from 'vue';

export interface MentionState {
  open: Ref<boolean>;
  anchorStart: number;
  anchorEnd: number;
}

/**
 * 输入事件处理（见 04 §1.3，含坑 3 / 4 / 5）。
 *
 * ============ 三个 handler 的分工 ============
 *  onCompositionStart/End → 由 useComposition 处理（document 级）
 *  onInput  → 只做"浮层触发判定"（坑 3：组合中跳过）
 *  onKeydown → 只做"发送/中断/浮层导航"（坑 1/2/7，在 composerKeys.ts）
 *  * 三者互不重叠，避免"两处都判断 isComposing 但判据不同"的 bug。
 * ==========================================
 */
export function createInputHandler(deps: {
  ta: Ref<HTMLTextAreaElement | null>;
  text: Ref<string>;
  isComposingNow: (e?: InputEvent) => boolean;
  onTrigger: (kind: 'mention' | 'slash', anchorStart: number, query: string) => void;
  onCloseOverlays: () => void;
}) {
  return function onInput(e: Event): void {
    const ev = e as InputEvent;

    // 坑 3：组合中不触发浮层
    if (deps.isComposingNow(ev)) return;

    const el = deps.ta.value;
    if (!el) return;

    // 坑 4：组合结束后 selectionStart 可能还没稳定，所以用 rAF 读
    requestAnimationFrame(() => {
      const pos = el.selectionStart;
      const before = deps.text.value.slice(0, pos);

      // 斜杠命令：只允许在文本最开头
      const slash = /^\/([a-z-]*)$/.exec(before);
      if (slash) {
        deps.onTrigger('slash', 0, slash[1] ?? '');
        return;
      }

      // @ 引用：`@` 前必须是行首或空白
      const atAll = /(^|[\s\n])@([^\s@]*)$/.exec(before);
      if (atAll) {
        const query = atAll[2] ?? '';
        const anchorStart = pos - query.length - 1;
        deps.onTrigger('mention', anchorStart, query);
        return;
      }

      // 都不匹配 → 关闭浮层
      deps.onCloseOverlays();
    });
  };
}
