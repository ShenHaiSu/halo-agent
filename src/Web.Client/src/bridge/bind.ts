// src/Web.Client/src/bridge/bind.ts
// D4-7 桥接绑定：幂等 await CefSharp.BindObjectAsync + 三态检测。

let bound = false;
let binding: Promise<void> | null = null;

/**
 * 绑定 CefSharp 的桥接对象。
 *
 * ============ 为什么必须 await ============
 *  CefSharp.BindObjectAsync 是异步的（跨进程向 browser 进程注册）。
 *  不 await 就调 window.haloHost.xxx 会得到 "undefined is not a function"。
 * ============ 为什么允许"没有宿主" ============
 *  纯浏览器开发（pnpm dev 直接打开）时 window.CefSharp 不存在，
 *  此时走 Mock，不能抛错。
 * ============ 幂等 ============
 *  重复调用返回同一个 Promise；已绑定则直接返回。
 * ============ ============
 */
export async function bindBridge(): Promise<void> {
  if (bound) return;
  if (binding) return binding;

  binding = (async () => {
    // 情况 ①：宿主环境
    if (window.CefSharp?.BindObjectAsync) {
      try {
        await window.CefSharp.BindObjectAsync('haloHost', 'haloAgent');
      } catch (e) {
        // 绑定失败不要抛（可能是页面加载太早），标记为"无桥接"，上层走 Mock。
        console.warn('[bridge] BindObjectAsync 失败，将使用 Mock', e);
      }
      // P0 兼容桥兜底（旧宿主）：失败静默忽略。
      try {
        await window.CefSharp.BindObjectAsync('hostBridge');
      } catch {
        /* 旧宿主无此桥 */
      }
    }

    bound = true;

    console.debug(`[bridge] 环境：host=${hasHost()} agent=${hasAgent()} mock=${isMock()}`);
  })();

  return binding;
}

/**
 * 检测桥接是否可用。
 *
 * ============ 为什么用 typeof === 'function' 而不是存在性判断 ============
 *  CefSharp 绑定失败时，window.haloHost 可能是"存在但方法全是 undefined"的壳对象。
 *  只判断 window.haloHost !== undefined 会误判。
 * ============ ============
 */
export function hasHost(): boolean {
  const h = window.haloHost;
  return typeof h === 'object' && h !== null && typeof h.systemInfo === 'function';
}

export function hasAgent(): boolean {
  const a = window.haloAgent;
  return (
    typeof a === 'object' &&
    a !== null &&
    typeof a.request === 'function' &&
    typeof a.pollEvents === 'function'
  );
}

/** 是否强制 Mock（开发时用 ?mock=1 或 localStorage） */
export function forceMock(): boolean {
  try {
    const q = new URLSearchParams(location.search);
    if (q.get('mock') === '1') return true;
    if (localStorage.getItem('haloagent.forceMock') === '1') return true;
  } catch {
    /* ignore（SSR/隐私模式） */
  }
  return false;
}

/** 是否走 Mock：生产包永不 mock；开发态需显式 ?mock=1。 */
export function isMock(): boolean {
  try {
    const proto = location.protocol;
    const isPackaged =
      proto === 'app:' || proto === 'file:' || proto === 'chrome-extension:';
    if (isPackaged) return false;
  } catch {
    return false;
  }
  return forceMock();
}

/** 仅测试用：重置绑定状态 */
export function __resetBridgeForTest(): void {
  bound = false;
  binding = null;
}
