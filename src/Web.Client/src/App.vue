<script setup lang="ts">
import { ref, onMounted } from 'vue'

type ThemeId = 'midnight' | 'aurora'

const theme = ref<ThemeId>('midnight')
const motionReduced = ref(false)
const hostInfo = ref<Record<string, unknown> | null>(null)
const bridgeError = ref('')
const pingResult = ref('')
const pingBusy = ref(false)

function applyTheme(next: ThemeId) {
  theme.value = next
  document.documentElement.dataset.theme = next
  localStorage.setItem('halo.theme', next)
}

function toggleMotion() {
  motionReduced.value = !motionReduced.value
  document.documentElement.dataset.motion = motionReduced.value ? 'reduced' : 'full'
}

async function pingEngine() {
  pingBusy.value = true
  pingResult.value = '请求中…'
  try {
    if (!window.CefSharp || !window.hostBridge) {
      pingResult.value = '无宿主桥：在宿主窗口内打开才能 ping 引擎（pnpm dev 直开模式无引擎）'
      return
    }
    await window.CefSharp.BindObjectAsync('hostBridge')
    const r = await window.hostBridge.enginePingAsync()
    pingResult.value = r.Ok
      ? `pong（${r.ElapsedMs}ms） ${r.Raw ?? ''}`
      : `失败（${r.ElapsedMs}ms）：${r.Error ?? '未知错误'}`
  } catch (e) {
    pingResult.value = `异常：${String(e)}`
  } finally {
    pingBusy.value = false
  }
}

onMounted(async () => {
  const saved = localStorage.getItem('halo.theme') as ThemeId | null
  if (saved === 'aurora' || saved === 'midnight') applyTheme(saved)

  try {
    if (window.CefSharp) {
      await window.CefSharp.BindObjectAsync('hostBridge')
      hostInfo.value = (await window.hostBridge?.getSystemInfoAsync()) ?? null
    } else {
      bridgeError.value = '当前是浏览器直开模式，没有宿主桥'
    }
  } catch (e) {
    bridgeError.value = String(e)
  }
})
</script>

<template>
  <div class="flex h-full flex-col">
    <header
      class="flex items-center gap-3 border-b border-line bg-surface px-4 py-2.5"
    >
      <span class="h-2.5 w-2.5 rounded-full bg-accent" />
      <h1 class="text-base font-medium tracking-wide">HaloAgent 骨架自检</h1>
      <div class="flex-1" />
      <button
        class="rounded border border-line px-3 py-1 text-sm transition-colors duration-fast hover:border-accent hover:text-accent"
        :title="'切换主题当前=' + theme"
        @click="applyTheme(theme === 'midnight' ? 'aurora' : 'midnight')"
      >
        切换主题
      </button>
      <button
        class="rounded border border-line px-3 py-1 text-sm transition-colors duration-fast hover:border-accent hover:text-accent"
        @click="toggleMotion"
      >
        动效：{{ motionReduced ? '关' : '开' }}
      </button>
    </header>

    <main class="selectable flex-1 overflow-auto p-6">
      <section
        class="mx-auto max-w-3xl rounded-lg border border-line bg-surface p-5"
      >
        <h2 class="mb-3 text-sm font-medium text-muted">宿主环境</h2>
        <p v-if="bridgeError" class="text-sm text-warn">{{ bridgeError }}</p>
        <pre
          v-else-if="hostInfo"
          class="overflow-auto rounded bg-canvas p-3 font-mono text-xs leading-relaxed"
          >{{ JSON.stringify(hostInfo, null, 2) }}</pre
        >
        <p v-else class="text-sm text-faint">读取中…</p>
      </section>

      <section
        class="mx-auto mt-4 max-w-3xl rounded-lg border border-line bg-surface p-5"
      >
        <h2 class="mb-3 text-sm font-medium text-muted">三方通道冒烟（页面 → 宿主 → 引擎 → 回程）</h2>
        <button
          class="rounded bg-accent px-4 py-1.5 text-sm text-accent-fg transition-opacity duration-fast hover:opacity-90 disabled:opacity-50"
          :disabled="pingBusy"
          @click="pingEngine"
        >
          {{ pingBusy ? 'ping 中…' : 'ping 引擎' }}
        </button>
        <p v-if="pingResult" class="selectable mt-3 break-all font-mono text-xs text-fg">{{ pingResult }}</p>
        <p v-else class="mt-3 text-xs text-faint">点击按钮，1 秒内应显示 pong 与耗时。</p>
      </section>

      <section
        class="mx-auto mt-4 max-w-3xl rounded-lg border border-line bg-surface p-5"
      >
        <h2 class="mb-3 text-sm font-medium text-muted">颜色样本（主题验证）</h2>
        <div class="grid grid-cols-4 gap-2">
          <div class="rounded bg-canvas p-3 text-center text-xs text-fg">
            canvas
          </div>
          <div class="rounded bg-surface p-3 text-center text-xs text-fg">
            surface
          </div>
          <div class="rounded bg-accent p-3 text-center text-xs text-accent-fg">
            accent
          </div>
          <div class="rounded bg-danger p-3 text-center text-xs text-white">
            danger
          </div>
        </div>
        <p class="mt-3 text-xs text-faint">
          若上面的色块都正常显示且切换主题会变色，说明 CSS 变量链路是通的。
        </p>
      </section>
    </main>
  </div>
</template>
