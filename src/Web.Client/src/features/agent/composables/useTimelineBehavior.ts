import { nextTick, onMounted, onUnmounted, watch } from 'vue';
import { getHostBridge } from '@shared/bridge/hostBridge';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { useAgentStream } from '@features/agent/stores/useAgentStream';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useUiStore } from '@/stores/uiStore';
import { isHandoffEnabled } from '@features/agent/utils/handoffGrouping';
import type { useTimelineMenu } from './useTimelineMenu';
import type { useTimelineWindow } from './useTimelineWindow';
import type { TimelineNav } from './useTimelineNav';

interface BehaviorDeps {
  win: ReturnType<typeof useTimelineWindow>;
  projection: { timelineItems: { value: Array<{ key: string }> } } | null;
  menu: ReturnType<typeof useTimelineMenu>;
  nav: TimelineNav;
}

/** 行为与生命周期：外链、重试、容器绑定、会话切换与跟随同步。 */
export function useTimelineBehavior(deps: BehaviorDeps) {
  const turn = useAgentTurn();
  const stream = useAgentStream();
  const workspaceStore = useWorkspaceStore();
  const uiStore = useUiStore();
  const { win, menu, nav } = deps;

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
      nav.setOrder(deps.projection?.timelineItems.value.map((i) => i.key) ?? []);
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
    () => deps.projection?.timelineItems.value.length ?? 0,
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
  // 交接窗内锁住「几何变化解除跟随」；窗结束时解锁并强制回底——此时高度已稳定，force 不再产生可见跳变。
  // 用户中途手势会把 handoffLocked 直接清掉：那种情况下这里不再 forceToBottom，控制权交还用户。
  watch(
    () => isHandoffEnabled() && !!turn.activeTurn?.liveCompleting && !!turn.activeTurn?.steadyReady,
    (active) => {
      if (active) {
        uiStore.lockHandoff();
        return;
      }
      if (!uiStore.handoffLocked) return;
      uiStore.releaseHandoffLock();
      if (win.paneVisible.value && win.follow.pinnedRef.value) void nextTick(() => win.pin.forceToBottom());
    }
  );
  watch(
    () => deps.projection?.timelineItems.value.map((i) => i.key) ?? [],
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
      nav.setOrder(deps.projection?.timelineItems.value.map((i) => i.key) ?? []);
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
