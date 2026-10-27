import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import './styles/theme.css'

// ---- ES2024+ API 的 polyfill 兜底 ----
// Chrome 109 没有 Object.groupBy / Promise.withResolvers / Array.fromAsync。
// 我们在源码里就避免使用它们（ESLint 规则会拦），但第三方库可能用到，
// 这里兜底一份最小实现，避免运行时 "is not a function"。
if (!('groupBy' in Object)) {
  // @ts-expect-error 故意补到 Object 上
  Object.groupBy = function groupBy<T>(items: Iterable<T>, fn: (x: T) => string) {
    const out: Record<string, T[]> = {}
    for (const item of items) {
      const k = fn(item)
      ;(out[k] ||= []).push(item)
    }
    return out
  }
}

const app = createApp(App)
app.use(createPinia())
app.mount('#app')

// ---- 启动自检：把宿主信息打到 console，出问题时一眼看到环境 ----
declare global {
  interface Window {
    // P0 兼容桥（保留）。
    hostBridge?: {
      // 注意（真机探针实测，CefSharp 109 + BindingOptions.DefaultBinder）：
      //   1. 方法名只做 camelCase，保留 Async 后缀：getSystemInfoAsync
      //   2. 方法返回的 DTO 字段名保持 C# 原样（PascalCase），不做 camelCase：
      //      { Ok, ElapsedMs, Raw, Error }、{ OsVersion, IsWin7, ... }
      getSystemInfoAsync: () => Promise<Record<string, unknown>>
      enginePingAsync: () => Promise<{ Ok: boolean; ElapsedMs: number; Raw?: string; Error?: string }>
    }
    // P3 双命名空间（11 + 3，见 docs/P3-宿主与CefSharp/02 §9）。
    haloHost?: {
      windowMinimize: () => Promise<void>
      windowMaximizeOrRestore: () => Promise<Record<string, unknown>>
      windowClose: () => Promise<void>
      windowState: () => Promise<Record<string, unknown>>
      systemInfo: () => Promise<Record<string, unknown>>
      openExternal: (url: string) => Promise<void>
      showItemInFolder: (path: string) => Promise<void>
      copyText: (text: string) => Promise<void>
      readClipboardImage: () => Promise<string | null>
      toggleDevTools: () => Promise<void>
      restartEngine: () => Promise<void>
    }
    haloAgent?: {
      request: (method: string, params: unknown) => Promise<unknown>
      pollEvents: (sinceSeq: number) => Promise<{ events: unknown[]; nextSeq: number }>
      isReady: () => Promise<boolean>
    }
    __haloHostEvent?: (kind: string, payload: unknown) => void
    CefSharp?: {
      BindObjectAsync: (...names: string[]) => Promise<void>
    }
  }
}

// P3 事件总线挂载点：宿主用 EvaluateScriptAsync 调 window.__haloHostEvent 推送
// host.windowState / host.engineStatus / host.filesDropped。
const hostEventBus = new Map<string, Set<(payload: unknown) => void>>()
window.__haloHostEvent = (kind: string, payload: unknown) => {
  const subs = hostEventBus.get(kind)
  if (subs) subs.forEach((fn) => {
    try { fn(payload) } catch (e) { console.warn('[halo] host 事件订阅者异常', e) }
  })
  document.dispatchEvent(new CustomEvent('halo-host-event', { detail: { kind, payload } }))
}

export function onHostEvent(kind: string, fn: (payload: unknown) => void) {
  let set = hostEventBus.get(kind)
  if (!set) hostEventBus.set(kind, (set = new Set()))
  set.add(fn)
  return () => { set.delete(fn) }
}

async function bootstrap() {
  if (!window.CefSharp) {
    console.info('[halo] 浏览器直开模式（无宿主桥）')
    return
  }
  try {
    // P3 双桥优先，P0 兼容桥兜底。
    await window.CefSharp.BindObjectAsync('haloHost', 'haloAgent')
    try { await window.CefSharp.BindObjectAsync('hostBridge') } catch { /* 旧宿主无此桥 */ }
    // systemInfo() 即“前端就绪”信号：宿主收到后补推积压事件。
    const sys = await window.haloHost?.systemInfo()
    console.info('[halo] 宿主环境', sys)
    if (sys && (sys as Record<string, unknown>).IsWindows7) {
      document.documentElement.dataset.motion = 'reduced'
      document.documentElement.dataset.os = 'win7'
    }
    const ready = await window.haloAgent?.isReady().catch(() => false)
    console.info('[halo] 引擎就绪', ready)
  } catch (e) {
    console.warn('[halo] 桥接失败', e)
  }
}

bootstrap()
