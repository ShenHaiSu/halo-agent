<template>
  <div ref="root" class="mermaid-block">
    <div v-if="state === 'idle' || state === 'loading'" role="status" aria-busy="true" class="mermaid-skeleton">
      <span class="mermaid-dot" /> <span class="mermaid-dot" /> <span class="mermaid-dot" />
    </div>
    <div v-else-if="state === 'ok'" ref="svgHost" role="img" class="mermaid-svg" v-html="svg" />
    <pre v-else role="status" class="mermaid-fallback">{{ fallbackText }}</pre>
    <div v-if="state !== 'idle'" class="mermaid-actions">
      <button type="button" @click="toggle">{{ showSource ? '显示图表' : '显示源码' }}</button>
      <button type="button" @click="copy">{{ copied ? '已复制' : '复制' }}</button>
    </div>
    <pre v-if="showSource" class="mermaid-source">{{ code }}</pre>
  </div>
</template>
<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';
import { MERMAID_CHAR_LIMIT, MERMAID_EDGE_LIMIT } from '@features/session/utils/renderContentLimits';

const props = defineProps<{ code: string }>();

type State = 'idle' | 'loading' | 'ok' | 'error';
const root = ref<HTMLElement | null>(null);
const svgHost = ref<HTMLElement | null>(null);
const state = ref<State>('idle');
const svg = ref('');
const showSource = ref(false);
const copied = ref(false);
const fallbackText = ref('');
let io: IntersectionObserver | null = null;
let cancelled = false;
let renderToken = 0;

// 全局串行队列：mermaid render 一次只跑一个
let queue: Promise<void> = Promise.resolve();
function enqueueRender<T>(fn: () => Promise<T>): Promise<T> {
  const next = queue.then(fn, fn) as Promise<T>;
  queue = next.then(
    () => undefined,
    () => undefined
  );
  return next;
}

function edgeCount(src: string): number {
  const m = src.match(/-->/g);
  return m ? m.length : 0;
}

function sanitizeSvg(raw: string): string | null {
  const trimmed = raw.trim();
  if (!trimmed.startsWith('<svg')) return null;
  // 最小 sanitize：拒绝 script/iframe/on*；DOMPurify 可用时优先
  if (/<script|on\w+\s*=|<iframe/i.test(trimmed)) return null;
  try {
    const purify = (window as unknown as { DOMPurify?: { sanitize: (s: string, o?: unknown) => string } }).DOMPurify;
    if (purify) return purify.sanitize(trimmed, { USE_PROFILES: { svg: true } });
  } catch {
    /* ignore */
  }
  return trimmed;
}

async function renderMermaid(src: string): Promise<string | null> {
  try {
    const mermaidSpec = 'mermaid';
    const mod = (await import(/* @vite-ignore */ mermaidSpec).catch(() => null)) as unknown as {
      default?: { render: (id: string, text: string) => Promise<{ svg: string }> };
      render?: (id: string, text: string) => Promise<{ svg: string }>;
    } | null;
    if (!mod) return null;
    const api = mod.default ?? mod;
    if (!api || typeof api.render !== 'function') return null;
    const id = `mmd-${Date.now().toString(36)}-${Math.floor(Math.random() * 1e6)}`;
    const out = await api.render(id, src);
    return out?.svg ?? null;
  } catch {
    return null;
  }
}

async function doRender(): Promise<void> {
  const src = props.code || '';
  if (src.length > MERMAID_CHAR_LIMIT || edgeCount(src) > MERMAID_EDGE_LIMIT) {
    state.value = 'error';
    fallbackText.value = `图过大（>${MERMAID_CHAR_LIMIT}ch 或边>${MERMAID_EDGE_LIMIT}），已回退源码`;
    return;
  }
  state.value = 'loading';
  const token = ++renderToken;
  const result = await enqueueRender(() => renderMermaid(src));
  if (cancelled || token !== renderToken) return;
  if (!result) {
    state.value = 'error';
    fallbackText.value = src;
    return;
  }
  const clean = sanitizeSvg(result);
  if (!clean) {
    state.value = 'error';
    fallbackText.value = src;
    return;
  }
  svg.value = clean;
  state.value = 'ok';
}

function toggle(): void {
  showSource.value = !showSource.value;
}

async function copy(): Promise<void> {
  try {
    await navigator.clipboard.writeText(props.code || '');
    copied.value = true;
    window.setTimeout(() => (copied.value = false), 1500);
  } catch {
    /* ignore */
  }
}

onMounted(() => {
  if (!root.value || typeof IntersectionObserver === 'undefined') {
    void doRender();
    return;
  }
  io = new IntersectionObserver(
    (entries) => {
      for (const en of entries) {
        if (en.isIntersecting) {
          io?.disconnect();
          io = null;
          void doRender();
          break;
        }
      }
    },
    { rootMargin: '240px 0px 240px 0px' }
  );
  io.observe(root.value);
});

onUnmounted(() => {
  cancelled = true;
  renderToken++;
  try {
    io?.disconnect();
  } catch {
    /* ignore */
  }
  io = null;
});
</script>
<style scoped>
.mermaid-skeleton {
  display: flex;
  gap: 6px;
  padding: 12px;
}
.mermaid-dot {
  width: 6px;
  height: 6px;
  border-radius: 9999px;
  background: currentColor;
  opacity: 0.4;
}
.mermaid-svg :deep(svg) {
  max-width: 100%;
  height: auto;
}
.mermaid-fallback,
.mermaid-source {
  white-space: pre-wrap;
  word-break: break-all;
}
</style>
