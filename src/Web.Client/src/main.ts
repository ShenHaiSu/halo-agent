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
    hostBridge?: {
      // 注意（真机探针实测，CefSharp 109 + BindingOptions.DefaultBinder）：
      //   1. 方法名只做 camelCase，保留 Async 后缀：getSystemInfoAsync
      //   2. 方法返回的 DTO 字段名保持 C# 原样（PascalCase），不做 camelCase：
      //      { Ok, ElapsedMs, Raw, Error }、{ OsVersion, IsWin7, ... }
      getSystemInfoAsync: () => Promise<Record<string, unknown>>
      enginePingAsync: () => Promise<{ Ok: boolean; ElapsedMs: number; Raw?: string; Error?: string }>
    }
    CefSharp?: {
      BindObjectAsync: (name: string) => Promise<void>
    }
  }
}

async function bootstrap() {
  if (!window.CefSharp) {
    console.info('[halo] 浏览器直开模式（无宿主桥）')
    return
  }
  try {
    await window.CefSharp.BindObjectAsync('hostBridge')
    const info = await window.hostBridge?.getSystemInfoAsync()
    console.info('[halo] 宿主环境', info)
    // 低配系统标记：前端可以据此关掉重动效（字段名见 Window 接口注释：PascalCase）
    if (info?.IsWin7) {
      document.documentElement.dataset.motion = 'reduced'
      document.documentElement.dataset.os = 'win7'
    }
  } catch (e) {
    console.warn('[halo] 桥接失败', e)
  }
}

bootstrap()
