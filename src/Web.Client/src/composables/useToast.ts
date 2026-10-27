// src/Web.Client/src/composables/useToast.ts
// D4-8 最小 toast 单例（模块级，供 ui store / 错误处理 / 画廊使用）。
// 完整 HToast 渲染组件由 D4-5 交付；此处先保证"有地方收 toast，不静默"。
import { ref } from 'vue';

export type ToastLevel = 'info' | 'success' | 'warning' | 'error';

export interface ToastAction {
  label: string;
  onClick: () => void;
}

export interface ToastOptions {
  duration?: number;
  action?: ToastAction;
}

export interface ToastItem extends ToastOptions {
  id: number;
  level: ToastLevel;
  text: string;
}

const DEFAULT_DURATION: Record<ToastLevel, number> = {
  info: 3000,
  success: 3000,
  warning: 4500,
  error: 6000,
};

const items = ref<ToastItem[]>([]);
let idCounter = 0;
const MAX_ITEMS = 3;

function push(level: ToastLevel, text: string, opts: ToastOptions = {}): number {
  const id = ++idCounter;
  items.value.push({
    id,
    level,
    text,
    duration: opts.duration ?? DEFAULT_DURATION[level],
    action: opts.action,
  });
  // 最多同时显示 3 条
  while (items.value.length > MAX_ITEMS) items.value.shift();
  const ms = opts.duration ?? DEFAULT_DURATION[level];
  if (ms > 0) {
    window.setTimeout(() => dismiss(id), ms);
  }
  return id;
}

function dismiss(id: number): void {
  items.value = items.value.filter((t) => t.id !== id);
}

function clear(): void {
  items.value = [];
}

export function useToast() {
  return {
    items,
    toasts: items,
    info: (text: string, opts?: ToastOptions) => push('info', text, opts),
    success: (text: string, opts?: ToastOptions) => push('success', text, opts),
    warning: (text: string, opts?: ToastOptions) => push('warning', text, opts),
    error: (text: string, opts?: ToastOptions) => push('error', text, opts),
    dismiss,
    clear,
  };
}

export type UseToast = ReturnType<typeof useToast>;
/** 兼容别名：早期代码可能 import { toasts } */
export const toasts = items;
