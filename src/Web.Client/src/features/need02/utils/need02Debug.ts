// need02-P0：观测埋点（DEV 限定）。
// 只有 `import.meta.env.DEV` 且 localStorage['halo-need02-debug'] === '1' 时才输出，
// 生产包零开销、零日志噪音。用法：
//   import { debug } from '@features/need02/utils/need02Debug';
//   debug('pending-insert', { requestId, at });

import { NEED02_DEBUG_KEY } from './need02Flags';

export function need02DebugEnabled(): boolean {
  try {
    if (!import.meta.env.DEV) return false;
    if (typeof window === 'undefined' || !window.localStorage) return false;
    return window.localStorage.getItem(NEED02_DEBUG_KEY) === '1';
  } catch {
    return false;
  }
}

/** DEV 调试埋点：`[need02] <tag>` + 可选载荷。未开启时直接返回。 */
export function debug(tag: string, payload?: unknown): void {
  if (!need02DebugEnabled()) return;
  if (payload === undefined) {
    console.debug(`[need02] ${tag}`);
    return;
  }
  console.debug(`[need02] ${tag}`, payload);
}
