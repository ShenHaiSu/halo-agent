import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import {
  DEFAULT_APPEARANCE,
  APPEARANCE_STORAGE_KEY,
  buildFontStack,
  buildMonoFontStack,
  normalizeHexColor,
  type AppearanceSettings,
  type ChatWidthMode,
  type ThemeMode,
} from '@/theme/appearance';

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
  return next;
}

export const useAppearanceStore = defineStore('appearance', () => {
  const settings = ref<AppearanceSettings>({ ...DEFAULT_APPEARANCE });

  const themeMode = computed<ThemeMode>(() => settings.value.themeMode);
  const themeColor = computed(() => settings.value.themeColor);
  const fontSize = computed(() => settings.value.fontSize);
  const chatWidthMode = computed<ChatWidthMode>(() => settings.value.chatWidthMode);

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
    root.style.setProperty('--accent-primary', s.themeColor);
  }

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

  function loadAndApply(): AppearanceSettings {
    load();
    applyToDom();
    return settings.value;
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
  }

  return {
    settings,
    themeMode,
    themeColor,
    fontSize,
    chatWidthMode,
    load,
    save,
    loadAndApply,
    update,
    setThemeMode,
    toggleTheme,
    reset,
    applyToDom,
  };
});
