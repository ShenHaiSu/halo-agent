import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import { getHostBridge } from '@shared/bridge/hostBridge';
import {
  DEFAULT_APPEARANCE,
  APPEARANCE_STORAGE_KEY,
  buildFontStack,
  buildMonoFontStack,
  normalizeHexColor,
  hexToRgbTriplet,
  type AppearanceSettings,
  type ChatWidthMode,
  type SendKeyMode,
  type ThemeMode,
} from '@shared/ui/appearance';

/** localStorage 读取 + 与默认值深合并（容错：坏 JSON / 越界值 / 未知字段忽略） */
function mergeWithDefaults(raw: unknown): AppearanceSettings {
  const next: AppearanceSettings = { ...DEFAULT_APPEARANCE };
  if (!raw || typeof raw !== 'object') return next;
  const obj = raw as Record<string, unknown>;

  if (obj.themeMode === 'dark' || obj.themeMode === 'light') {
    next.themeMode = obj.themeMode;
  }
  const hex = normalizeHexColor(obj.themeColor);
  if (hex) next.themeColor = hex;
  if (typeof obj.fontSize === 'number' && Number.isFinite(obj.fontSize)) {
    next.fontSize = Math.min(16, Math.max(12, Math.round(obj.fontSize)));
  }
  if (typeof obj.chineseFont === 'string' && obj.chineseFont.trim()) {
    next.chineseFont = obj.chineseFont.trim();
  }
  if (typeof obj.englishFont === 'string' && obj.englishFont.trim()) {
    next.englishFont = obj.englishFont.trim();
  }
  if (typeof obj.monoFont === 'string' && obj.monoFont.trim()) {
    next.monoFont = obj.monoFont.trim();
  }
  if (obj.chatWidthMode === 'centered' || obj.chatWidthMode === 'full') {
    next.chatWidthMode = obj.chatWidthMode;
  }
  if (obj.sendKeyMode === 'enter' || obj.sendKeyMode === 'ctrl-enter') {
    next.sendKeyMode = obj.sendKeyMode;
  }
  return next;
}

export const useAppearanceStore = defineStore('appearance', () => {
  const settings = ref<AppearanceSettings>({ ...DEFAULT_APPEARANCE });
  // 后端落盘 debounce 计时器（拖动字号/主题色滑块时避免每帧写磁盘）
  let persistTimer: number | null = null;

  const themeMode = computed<ThemeMode>(() => settings.value.themeMode);
  const themeColor = computed(() => settings.value.themeColor);
  const fontSize = computed(() => settings.value.fontSize);
  const chatWidthMode = computed<ChatWidthMode>(() => settings.value.chatWidthMode);
  const sendKeyMode = computed<SendKeyMode>(() => settings.value.sendKeyMode);

  function applyToDom(): void {
    if (typeof document === 'undefined') return;
    const root = document.documentElement;
    const s = settings.value;
    // ① 暗亮 class（与 systemStore.applyThemeClass 同语义，单真相源在此）
    if (s.themeMode === 'light') {
      root.classList.remove('dark');
      root.classList.add('light');
    } else {
      root.classList.remove('light');
      root.classList.add('dark');
    }
    // ② CSS 变量：字体栈 / 等宽栈 / 基准字号 / 主题色
    root.style.setProperty('--font-sans', buildFontStack(s));
    root.style.setProperty('--font-mono', buildMonoFontStack(s.monoFont));
    root.style.setProperty('--app-font-size', `${s.fontSize}px`);
    // 主题色可运行时自定义：hex 与 rgb 三元组必须同步写，否则 accent/<alpha> 系列
    // （rgb(var(--accent-primary-rgb) / 0.4)）会停留在默认蓝，表现为自定义色失效。
    const accent = normalizeHexColor(s.themeColor) ?? '#3b82f6';
    root.style.setProperty('--accent-primary', accent);
    root.style.setProperty('--accent-primary-rgb', hexToRgbTriplet(accent) ?? '59 130 246');
  }

  /**
   * 写 localStorage 同步快照：仅供 index.html 首帧脚本同步读取 dark/light，
   * 真相源已是后端 config/appearance.json。保留快照可避免首帧主题闪烁。
   */
  function save(): void {
    try {
      localStorage.setItem(APPEARANCE_STORAGE_KEY, JSON.stringify(settings.value));
    } catch {
      // 配额/隐私模式下静默忽略：内存态仍可即时预览
    }
  }

  function load(): AppearanceSettings {
    let raw: unknown = null;
    try {
      const text = localStorage.getItem(APPEARANCE_STORAGE_KEY);
      if (text) raw = JSON.parse(text);
    } catch {
      raw = null;
    }
    settings.value = mergeWithDefaults(raw);
    return settings.value;
  }

  /**
   * ① 同步应用 localStorage 快照（与 index.html 首帧同值，杜绝闪烁）；
   * ② 异步以后端 config/appearance.json 覆盖（真相源）；
   * ③ 后端无文件/通道缺失时把快照回填（首次升级的一次性迁移），此后后端为准。
   * 注意 dev(http://localhost:5173) 与 release(app://local) 的 localStorage 按 origin 隔离，
   * 所以迁移只能在「当前运行的 origin」上发生，配置互通靠第 ② 步的后端文件。
   */
  async function loadAndApply(): Promise<AppearanceSettings> {
    load();
    applyToDom();
    try {
      const res = await getHostBridge().getAppearance();
      if (res.ok && res.data) {
        settings.value = mergeWithDefaults(res.data);
        save(); // 刷新快照，让 index.html 下次首帧直接拿到后端值
        applyToDom();
      } else {
        // APPEARANCE_NOT_FOUND / 通道不支持：把当前快照迁移到后端
        await flush();
      }
    } catch {
      // 桥未连接（Mock / 生产无桥）：保留 localStorage 快照，不阻断启动
      await flush();
    }
    return settings.value;
  }

  /** 立即把当前设置写回后端；debounce 到期与页面隐藏时调用。失败静默，快照仍在。 */
  async function flush(): Promise<void> {
    if (persistTimer !== null) {
      window.clearTimeout(persistTimer);
      persistTimer = null;
    }
    try {
      await getHostBridge().setAppearance(JSON.stringify(settings.value));
    } catch {
      /* 后端写入失败不阻断 UI，下次启动可由 loadAndApply 重试迁移 */
    }
  }

  /** 300ms debounce：避免连续 update（拖滑块）时每帧写磁盘。 */
  function schedulePersist(): void {
    if (typeof window === 'undefined') return;
    if (persistTimer !== null) window.clearTimeout(persistTimer);
    persistTimer = window.setTimeout(() => void flush(), 300);
  }

  function update(patch: Partial<AppearanceSettings>): void {
    const merged: AppearanceSettings = mergeWithDefaults({ ...settings.value, ...patch });
    // update 走合并校验：若 patch 含非法 hex 则保持旧主题色（调用方负责 warning）
    if (patch.themeColor !== undefined && !normalizeHexColor(patch.themeColor)) {
      merged.themeColor = settings.value.themeColor;
    }
    settings.value = merged;
    save();
    applyToDom();
    schedulePersist();
  }

  function setThemeMode(mode: ThemeMode): void {
    update({ themeMode: mode });
  }

  function toggleTheme(): void {
    setThemeMode(settings.value.themeMode === 'dark' ? 'light' : 'dark');
  }

  function reset(): void {
    settings.value = { ...DEFAULT_APPEARANCE };
    save();
    applyToDom();
    schedulePersist();
  }

  return {
    settings,
    themeMode,
    themeColor,
    fontSize,
    chatWidthMode,
    sendKeyMode,
    load,
    save,
    loadAndApply,
    update,
    setThemeMode,
    toggleTheme,
    reset,
    applyToDom,
    flush,
  };
});
