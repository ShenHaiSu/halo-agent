/**
 * Native frame handle: dual-bind `hostBridge` + `hostFrame` and keep the
 * raw frame object reachable after the legacy facade is wrapped.
 * Standalone (no imports) so both `shared/bridge` and `transport` can use it
 * without creating an import cycle.
 */

const WRAPPED_MARK = '__haloWrappedApi';

export interface NativeFrameScope {
  CefSharp?: { BindObjectAsync: (name: string) => Promise<void> };
  hostBridge?: unknown;
  hostFrame?: unknown;
  __nativeHostFrame?: unknown;
}

function scopeOf(): NativeFrameScope {
  return globalThis as unknown as NativeFrameScope;
}

function isWrapped(value: unknown): boolean {
  return !!value && (value as Record<string, unknown>)[WRAPPED_MARK] === true;
}

function asUsable(value: unknown): Record<string, unknown> | null {
  if (!value || typeof value !== 'object' || isWrapped(value)) return null;
  return value as Record<string, unknown>;
}

let bindOnce: Promise<unknown | null> | null = null;

/**
 * Bind both Cef objects once. One side failing never blocks the other;
 * callers still get whatever is usable. Never throws.
 */
export function ensureNativeFrame(): Promise<unknown | null> {
  if (bindOnce) return bindOnce;
  bindOnce = (async () => {
    const scope = scopeOf();
    try {
      const binder = scope.CefSharp?.BindObjectAsync;
      if (typeof binder === 'function') {
        const needBridge = !asUsable(scope.hostBridge);
        const needFrame = !asUsable(scope.hostFrame ?? scope.__nativeHostFrame);
        if (needBridge || needFrame) {
          const jobs: Promise<unknown>[] = [];
          if (needBridge) {
            jobs.push(
              binder
                .call(scope.CefSharp, 'hostBridge')
                .catch((err: unknown) => console.error('[nativeFrame] BindObjectAsync(hostBridge) failed:', err))
            );
          }
          if (needFrame) {
            jobs.push(
              binder
                .call(scope.CefSharp, 'hostFrame')
                .catch((err: unknown) => console.error('[nativeFrame] BindObjectAsync(hostFrame) failed:', err))
            );
          }
          await Promise.allSettled(jobs);
        }
      }
    } catch (err) {
      console.error('[nativeFrame] ensureNativeFrame failed:', err);
    }
    const usable = getNativeFrame();
    if (usable) cacheNativeFrame(usable);
    return usable;
  })();
  return bindOnce;
}

/** Raw frame object for `invoke`/`getEventBatch`; null when unusable/wrapped. */
export function getNativeFrame(): Record<string, unknown> | null {
  try {
    const scope = scopeOf();
    return asUsable(scope.__nativeHostFrame) ?? asUsable(scope.hostFrame) ?? asUsable(scope.hostBridge);
  } catch {
    return null;
  }
}

/** Freeze the resolved handle so later `window.hostBridge = wrappedApi` cannot hide it. */
export function cacheNativeFrame(value: unknown): void {
  try {
    if (value && typeof value === 'object' && !isWrapped(value)) {
      scopeOf().__nativeHostFrame = value;
    }
  } catch {
    /* cache is best effort */
  }
}

/** Test-only reset for the once promise. */
export function resetNativeFrameForTest(): void {
  bindOnce = null;
}
