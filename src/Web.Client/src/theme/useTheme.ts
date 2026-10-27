// src/Web.Client/src/theme/useTheme.ts
import { computed, ref, watch } from 'vue';
import { applyThemeTokens, clearInlineThemeTokens } from './apply';
import { validateThemeTokens } from './validate';
import type { ThemeFile } from './types';
import { builtinThemes } from './builtin';

export type ThemeMode = 'light' | 'dark' | 'system' | string;

const STORAGE_KEY = 'haloagent.theme';

/**
 * 当前主题状态（模块级单例）。
 */
const currentMode = ref<ThemeMode>('light');
const currentThemeId = ref<string>('light');
const currentTheme = ref<ThemeFile>(builtinThemes.light!);
const loading = ref(false);
const lastError = ref<string | null>(null);

const systemPrefersDark = ref(false);

/**
 * 纯函数：由 mode + 系统深色偏好解析出主题 id。
 * 提取为纯函数以便单元测试（applyResolved 内部改用它）。
 */
export function resolveThemeId(mode: string, systemDark: boolean): string {
  if (mode === 'system') return systemDark ? 'dark' : 'light';
  return String(mode);
}

/** 初始化：只调用一次（main.ts） */
export function initTheme(): void {
  // ① 监听系统深色模式（仅 Win10/11 有意义；Win7 恒为 false）
  try {
    const mq = window.matchMedia('(prefers-color-scheme: dark)');
    systemPrefersDark.value = mq.matches;
    mq.addEventListener('change', (e) => {
      systemPrefersDark.value = e.matches;
      if (currentMode.value === 'system') {
        void applyResolved();
      }
    });
  } catch {
    // Win7 上 matchMedia 可能存在但不支持该 query，忽略
  }

  // ② 从 localStorage 读用户上次的选择
  let saved: string | null = null;
  try {
    saved = localStorage.getItem(STORAGE_KEY);
  } catch {
    saved = null;
  }
  currentMode.value = (saved as ThemeMode) ?? 'light';

  // ③ 应用
  void applyResolved();

  // ④ 模式变化时保存并应用
  watch(currentMode, (m) => {
    try {
      localStorage.setItem(STORAGE_KEY, String(m));
    } catch {
      /* 隐私模式下可能失败，忽略 */
    }
    void applyResolved();
  });
}

async function applyResolved(): Promise<void> {
  const mode = currentMode.value;

  const themeId = resolveThemeId(String(mode), systemPrefersDark.value);

  if (themeId === currentThemeId.value && currentTheme.value) return;

  loading.value = true;
  lastError.value = null;
  try {
    const theme = await loadTheme(themeId);
    currentTheme.value = theme;
    currentThemeId.value = themeId;

    // * 关键：清除旧的内联覆盖，再应用新主题
    clearInlineThemeTokens();
    if (theme.tokens && Object.keys(theme.tokens).length > 0) {
      const result = validateThemeTokens(theme.tokens);
      if (!result.ok) {
        lastError.value = result.errors.join('；');
      }
      applyThemeTokens(result.tokens);
    }

    // 设置 data-theme 供 CSS 里少数需要区分的场景使用
    // （例如 `<html data-theme="dark">` 下给滚动条换深色配色）
    document.documentElement.dataset.theme = themeId;

    // Windows 标题栏的深色标志（可选，需宿主配合）
    document.documentElement.style.colorScheme = isDark(themeId) ? 'dark' : 'light';
  } catch (e) {
    lastError.value = e instanceof Error ? e.message : String(e);
    // 失败时回退到内置 light：清掉旧覆盖并把 light 的值写成内联，
    // 保证界面仍可读（绝不出现白底白字），themeId 保持为 light。
    currentTheme.value = builtinThemes.light!;
    currentThemeId.value = 'light';
    clearInlineThemeTokens();
    applyThemeTokens(builtinThemes.light!.tokens);
  } finally {
    loading.value = false;
  }
}

function isDark(id: string): boolean {
  const t = builtinThemes[id];
  if (t?.basedOn === 'dark') return true;
  // 用背景亮度粗略判断
  const bg = t?.tokens['bg'];
  if (typeof bg === 'string') {
    const m = /(\d+(?:\.\d+)?)%\s*$/.exec(bg.trim());
    if (m) return Number(m[1]) < 50;
  }
  return id === 'dark' || id.includes('dark') || id.includes('midnight');
}

async function loadTheme(id: string): Promise<ThemeFile> {
  // 内置主题直接从 bundle 里拿
  if (builtinThemes[id]) return builtinThemes[id]!;

  // 用户主题：通过引擎读文件（P3 已有 asset.readTheme）
  const raw = (await window.haloAgent?.request('asset.readTheme', { id })) as unknown;
  if (typeof raw !== 'object' || raw === null) {
    throw new Error(`主题 ${id} 读取失败`);
  }
  const file = raw as ThemeFile;
  const check = validateThemeTokens(file.tokens);
  if (!check.ok) {
    throw new Error(`主题 ${id} 校验失败：${check.errors.join('；')}`);
  }
  return file;
}

export function useTheme() {
  return {
    mode: currentMode,
    themeId: currentThemeId,
    theme: currentTheme,
    loading,
    error: lastError,
    isDark: computed(() => isDark(currentThemeId.value)),
    setMode(m: ThemeMode) {
      currentMode.value = m;
    },
    /** 列出所有可用主题（内置 + 用户） */
    async listThemes(): Promise<ThemeFile[]> {
      const res = (await window.haloAgent?.request('asset.listThemes', null)) as unknown;
      const userThemes = Array.isArray(res) ? (res as ThemeFile[]) : [];
      return [...Object.values(builtinThemes), ...userThemes];
    },
  };
}
