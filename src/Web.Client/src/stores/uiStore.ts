/**
 * need19：右侧边栏（Drawer）UI 状态。仅“界面偏好”，不承载业务数据。
 * 持久化键：halo.ui.rightRail.v1（open 不持久化）
 */
import { defineStore } from 'pinia';
import { ref, watch } from 'vue';

export type RightRailTab = 'goal' | 'toc';
export type TocRoleFilter = 'all' | 'user' | 'assistant';

const STORAGE_KEY = 'halo.ui.rightRail.v1';
const MIN_W = 320;
const MAX_W = 720;

/** need22-P5 J7：持久化 UI 偏好类型（对外导出供调试/复用，行为不变）。 */
export interface Persisted {
  tab: RightRailTab;
  width: number;
  hintShown: boolean;
  tocRole: TocRoleFilter;
  tocRoundHeadsOnly: boolean;
  tocSkipSystem: boolean;
}

function clampWidth(v: unknown): number {
  const n = Number(v);
  if (!Number.isFinite(n)) return 360;
  return Math.max(MIN_W, Math.min(MAX_W, Math.round(n)));
}

function loadPersisted(): Persisted {
  const fallback: Persisted = {
    tab: 'toc', width: 360, hintShown: false,
    tocRole: 'all', tocRoundHeadsOnly: false, tocSkipSystem: true,
  };
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return fallback;
    const o = JSON.parse(raw) as Partial<Persisted>;
    return {
      tab: o.tab === 'goal' || o.tab === 'toc' ? o.tab : fallback.tab,
      width: clampWidth(o.width),
      hintShown: o.hintShown === true,
      tocRole: o.tocRole === 'user' || o.tocRole === 'assistant' ? o.tocRole : 'all',
      tocRoundHeadsOnly: o.tocRoundHeadsOnly === true,
      tocSkipSystem: o.tocSkipSystem !== false,
    };
  } catch {
    return fallback;
  }
}

export const useUiStore = defineStore('ui', () => {
  const persisted = loadPersisted();

  /** Drawer 是否打开（不持久化：启动默认关闭，避免遮挡主界面） */
  const rightRailOpen = ref(false);
  const rightRailTab = ref<RightRailTab>(persisted.tab);
  const rightRailWidth = ref(clampWidth(persisted.width));
  const hintShown = ref(persisted.hintShown);

  /** 目录过滤 */
  const tocRole = ref<TocRoleFilter>(persisted.tocRole);
  const tocRoundHeadsOnly = ref(persisted.tocRoundHeadsOnly);
  const tocSkipSystem = ref(persisted.tocSkipSystem);

  /** 跳转锁：performance.now() 时间戳；期内禁止“新消息自动滚到底” */
  const jumpLockUntil = ref(0);

  function open(tab?: RightRailTab): void {
    if (tab) rightRailTab.value = tab;
    rightRailOpen.value = true;
  }
  function close(): void { rightRailOpen.value = false; }
  function toggle(tab?: RightRailTab): void {
    if (rightRailOpen.value && (!tab || tab === rightRailTab.value)) { close(); return; }
    open(tab);
  }
  function setWidth(w: number): void { rightRailWidth.value = clampWidth(w); }
  function lockJump(ms = 1500): void { jumpLockUntil.value = performance.now() + ms; }
  function releaseJumpLock(): void { jumpLockUntil.value = 0; }
  function markHintShown(): void { hintShown.value = true; }

  // 节流持久化（100ms），避免拖拽时每帧写 localStorage
  let timer: number | null = null;
  watch(
    [rightRailTab, rightRailWidth, hintShown, tocRole, tocRoundHeadsOnly, tocSkipSystem],
    () => {
      if (timer !== null) window.clearTimeout(timer);
      timer = window.setTimeout(() => {
        timer = null;
        try {
          const data: Persisted = {
            tab: rightRailTab.value,
            width: rightRailWidth.value,
            hintShown: hintShown.value,
            tocRole: tocRole.value,
            tocRoundHeadsOnly: tocRoundHeadsOnly.value,
            tocSkipSystem: tocSkipSystem.value,
          };
          localStorage.setItem(STORAGE_KEY, JSON.stringify(data));
        } catch { /* 隐私模式/配额满：仅本次会话内有效 */ }
      }, 100);
    }
  );

  return {
    rightRailOpen, rightRailTab, rightRailWidth, hintShown,
    tocRole, tocRoundHeadsOnly, tocSkipSystem,
    jumpLockUntil,
    open, close, toggle, setWidth, lockJump, releaseJumpLock, markHintShown,
  };
});
