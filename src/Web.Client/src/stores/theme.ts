// src/Web.Client/src/stores/theme.ts
// D4-8 主题 store（对 theme/useTheme 的 Pinia 包装）。
import { defineStore } from 'pinia';
import { computed } from 'vue';
import { useTheme } from '@/theme/useTheme';

/**
 * 主题 store（对 theme/useTheme 的 Pinia 包装）。
 *
 * ============ 为什么要包一层 ============
 *  useTheme 是"模块级单例"（不依赖 Pinia），
 *  但 Vue 组件里更习惯用 store。
 *  包一层零成本，且让"主题"和其他 store 用法一致。
 * ============ ============
 */
export const useThemeStore = defineStore('theme', () => {
  const t = useTheme();

  return {
    mode: t.mode,
    themeId: t.themeId,
    theme: t.theme,
    loading: t.loading,
    error: t.error,
    isDark: computed(() => t.isDark.value),
    setMode: t.setMode,
    listThemes: t.listThemes,
  };
});
