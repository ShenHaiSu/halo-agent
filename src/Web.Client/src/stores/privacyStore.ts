import { defineStore } from 'pinia';
import { ref } from 'vue';

/**
 * 隐私/展示保护开关（前端本地真相源，持久化于 localStorage['halo.privacy.v1']）。
 * 目前仅一项：用量分析中把 Provider 名替换为 5 位随机代号。
 */

export interface PrivacySettings {
  /** 模糊化 Provider 名称（默认开启） */
  maskProviderName: boolean;
}

export const DEFAULT_PRIVACY: PrivacySettings = {
  /** 默认开启：不外泄 Provider 真名 */
  maskProviderName: true,
};

export const PRIVACY_STORAGE_KEY = 'halo.privacy.v1';

/** 容错合并：坏 JSON / 非对象 / 非法字段 → 回落默认（true），保证“默认开启”不被破坏 */
function mergeWithDefaults(raw: unknown): PrivacySettings {
  const next: PrivacySettings = { ...DEFAULT_PRIVACY };
  if (!raw || typeof raw !== 'object') return next;
  const obj = raw as Record<string, unknown>;
  if (typeof obj.maskProviderName === 'boolean') next.maskProviderName = obj.maskProviderName;
  return next;
}

export const usePrivacyStore = defineStore('privacy', () => {
  const settings = ref<PrivacySettings>({ ...DEFAULT_PRIVACY });
  const loaded = ref(false);

  function load(): PrivacySettings {
    let raw: unknown = null;
    try {
      const text = localStorage.getItem(PRIVACY_STORAGE_KEY);
      if (text) raw = JSON.parse(text);
    } catch {
      raw = null;
    }
    settings.value = mergeWithDefaults(raw);
    loaded.value = true;
    return settings.value;
  }

  function save(): void {
    try {
      localStorage.setItem(PRIVACY_STORAGE_KEY, JSON.stringify(settings.value));
    } catch {
      // 配额/隐私模式：内存态仍生效
    }
  }

  /** 首次使用自动 load，避免组件忘写初始化导致默认值不符 */
  function ensureLoaded(): void {
    if (!loaded.value) load();
  }

  function setMaskProviderName(enabled: boolean): void {
    settings.value = { ...settings.value, maskProviderName: enabled };
    save();
  }

  function toggleMaskProviderName(): void {
    setMaskProviderName(!settings.value.maskProviderName);
  }

  function reset(): void {
    settings.value = { ...DEFAULT_PRIVACY };
    save();
  }

  return {
    settings,
    loaded,
    load,
    save,
    ensureLoaded,
    setMaskProviderName,
    toggleMaskProviderName,
    reset,
  };
});
