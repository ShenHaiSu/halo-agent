import { nextTick, onMounted, onUnmounted, watch } from 'vue';
import { getHostBridge } from '@shared/bridge/hostBridge';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { useAgentStream } from '@features/agent/stores/useAgentStream';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import type { useTimelineMenu } from './useTimelineMenu';
import type { useTimelineWindow } from './useTimelineWindow';
import type { useTimelineProjection } from './useTimelineProjection';
import type { TimelineNav } from './useTimelineNav';

interface BehaviorDeps {
  win: ReturnType<typeof useTimelineWindow>;
  projection: ReturnType<typeof useTimelineProjection>;
  menu: ReturnType<typeof useTimelineMenu>;
  nav: TimelineNav;
}

/** 行为与生命周期：外链、重试、容器绑定、会话切换与跟随同步。 */
export function useTimelineBehavior(deps: BehaviorDeps) {
  const turn = useAgentTurn();
  const stream = useAgentStream();
  const workspaceStore = useWorkspaceStore();
  const { win, projection, menu, nav } = deps;

  async function onMarkdownLinkClick(e: MouseEvent): Promise<void> {
    const el = e.target as HTMLElement | null;
    const anchor = el?.closest?.('a[href]') as HTMLAnchorElement | null;
    if (!anchor) return;
    const href = (anchor.getAttribute('href') || '').trim();
    if (!href || href.startsWith('#')) return;
    e.preventDefault();
    e.stopPropagation();
    try {
      const res = await getHostBridge().openExternalLink(href, workspaceStore.activeWorkspace?.rootPath || '');
      if (!res.ok) console.warn('[markdown-link] open failed:', res.code, res.message);
    } catch (err) {
      console.warn('[markdown-link] open failed:', err);
    }
  }
  async function onRetryTurn(): Promise<void> {
    win.clearReadingWindow();
    if (win.paneVisible.value) win.follow.onSendRisingEdge(() => win.pin.forceToBottom());
    try {
      await turn.retryLastTurn();
    } catch (err) {
      console.warn('[timeline] retry failed:', err);
    }
  }
  function onDismissError(): void {
    try {
      turn.clearLastError();
    } catch (e) {
      console.warn('[timeline] dismiss failed:', e);
    }
  }
  function attachContainer(el: HTMLElement | null): void {
    win.scrollEl.value = el;
    try {
      nav.setContainer(el);
      nav.setOrder(projection.timelineItems.value.map((i) => i.key));
    } catch (e) {
      console.warn('[timeline] nav init failed:', e);
    }
    win.pin.bindUserScroll();
    win.bindScrollWindow();
    win.ensureMountWindow();
  }
  function detachContainer(): void {
    win.pin.unbindUserScroll();
    win.unbindScrollWindow();
    win.pin.cancelScheduled();
    try {
      nav.clear();
      nav.setContainer(null);
    } catch (e) {
      console.warn('[timeline] nav clear failed:', e);
    }
    win.scrollEl.value = null;
  }
  let sessionSwitchGuard = false;
  watch(
    () => workspaceStore.activeSessionId,
    () => {
      menu.closeMenu();
      win.clearReadingWindow();
      sessionSwitchGuard = true;
      win.follow.releaseFollow();
      win.resetMountWindow();
      if (win.scrollEl.value) win.scrollEl.value.scrollTop = 0;
    }
  );
  watch(
    () => projection.timelineItems.value.length,
    () => {
      if (sessionSwitchGuard) {
        sessionSwitchGuard = false;
        win.resetMountWindow();
        if (win.scrollEl.value) win.scrollEl.value.scrollTop = 0;
        return;
      }
      if (!win.paneVisible.value || !win.follow.pinnedRef.value) return;
      void nextTick(() => win.pin.scheduleToBottom());
    }
  );
  watch(
    () => (stream.streamingText?.length || 0) + (stream.streamingReasoning?.length || 0),
    (len, prev) => {
      if (!len || (prev !== undefined && len <= prev)) return;
      if (!win.paneVisible.value || !win.follow.pinnedRef.value) return;
      if (win.readingWindow.value) return;
      win.pin.scheduleToBottom();
    }
  );
  watch(
    () => stream.hasActiveTurn,
    (v, prev) => {
      if (prev && !v && win.paneVisible.value && win.follow.pinnedRef.value) win.pin.scheduleToBottom();
    }
  );
  watch(
    () => projection.timelineItems.value.map((i) => i.key),
    (keys) => {
      try {
        nav.setOrder(keys);
      } catch (e) {
        console.warn('[timeline] nav order failed:', e);
      }
    },
    { immediate: true }
  );
  onMounted(() => {
    if (win.paneVisible.value && win.follow.pinnedRef.value) void nextTick(() => win.pin.scheduleToBottom());
    menu.bindMenuGlobalClose();
    try {
      nav.setOrder(projection.timelineItems.value.map((i) => i.key));
    } catch (e) {
      console.warn('[timeline] nav init failed:', e);
    }
  });
  onUnmounted(() => {
    detachContainer();
    win.pin.cancelScheduled();
  });
  return { onMarkdownLinkClick, onRetryTurn, onDismissError, attachContainer, detachContainer };
}
export type TimelineBehavior = ReturnType<typeof useTimelineBehavior>;
