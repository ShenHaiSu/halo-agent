// src/Web.Client/src/stores/boot.ts
// 启动门（boot gate）：dist 实测前端常比引擎先就绪，首屏请求直接打过去只会收获
// 一片“引擎桥接不可用”类误报。用纯前端绕行：全屏启动动画盖住首屏 + 把初始数据
// 加载推迟到引擎就绪（或超时降级）后再发；门内的失败一律静默，只记日志。
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { transport } from '@/transport';

export type BootPhase = 'boot' | 'ready' | 'degraded';

export interface BootWaitOptions {
  pollMs?: number;
  timeoutMs?: number;
  minSplashMs?: number;
}

export const BOOT_POLL_MS = 150;
export const BOOT_TIMEOUT_MS = 12000;
export const BOOT_MIN_SPLASH_MS = 2000;
/** 超过这么久还没就绪，提示文案切换为“热身中”，避免用户以为卡死。 */
const WARM_NOTE_MS = 4000;

function sleep(ms: number): Promise<void> {
  return new Promise<void>((resolve) => globalThis.setTimeout(resolve, ms));
}

export const useBootStore = defineStore('boot', () => {
  const phase = ref<BootPhase>('boot');
  const note = ref('正在连接本地引擎…');
  /** 门已过（含降级放行）：首屏请求可以发了。 */
  const ready = computed(() => phase.value !== 'boot');
  const isBooting = computed(() => phase.value === 'boot');

  // 幂等：App 与后挂载组件重复调用时复用同一 Promise，不重复轮询。
  let gate: Promise<BootPhase> | null = null;

  function waitForEngine(opts: BootWaitOptions = {}): Promise<BootPhase> {
    if (gate) return gate;
    const pollMs = opts.pollMs ?? BOOT_POLL_MS;
    const timeoutMs = opts.timeoutMs ?? BOOT_TIMEOUT_MS;
    const minSplashMs = opts.minSplashMs ?? BOOT_MIN_SPLASH_MS;
    gate = (async () => {
      // Mock / 纯浏览器环境没有引擎可等，直接放行（动画只闪一下）。
      if (transport.isMock) {
        phase.value = 'ready';
        return phase.value;
      }
      const t0 = Date.now();
      for (;;) {
        let ok = false;
        try {
          ok = await transport.isReady();
        } catch {
          ok = false;
        }
        if (ok) break;
        if (Date.now() - t0 >= timeoutMs) {
          phase.value = 'degraded';
          note.value = '本地引擎启动超时，已进入降级模式';
          console.warn('[boot] 等待引擎超时，已降级放行');
          return phase.value;
        }
        if (Date.now() - t0 >= WARM_NOTE_MS) note.value = '本地引擎还在热身，请稍候…';
        await sleep(pollMs);
      }
      // 最小展示时长：引擎就绪极快时也让动画完整闪过一帧，避免首屏闪烁。
      const elapsed = Date.now() - t0;
      if (elapsed < minSplashMs) await sleep(minSplashMs - elapsed);
      note.value = '正在加载工作区…';
      phase.value = 'ready';
      return phase.value;
    })();
    return gate;
  }

  return { phase, note, ready, isBooting, waitForEngine };
});
