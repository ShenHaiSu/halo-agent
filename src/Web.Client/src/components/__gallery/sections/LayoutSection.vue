<!-- src/Web.Client/src/components/__gallery/sections/LayoutSection.vue -->
<!-- D4-9 画廊：布局分区（Splitter 演示 + 布局 token + 断点）。 -->
<script setup lang="ts">
import { ref } from 'vue';

const w = ref(240);
const dragging = ref(false);

function onDragStart() {
  dragging.value = true;
  document.body.style.cursor = 'col-resize';
  document.body.style.userSelect = 'none';
}
function onDragEnd() {
  dragging.value = false;
  document.body.style.cursor = '';
  document.body.style.userSelect = '';
}

function onSplitterDown(e: PointerEvent) {
  if (e.button !== 0) return;
  onDragStart();
  const startX = e.clientX;
  const startW = w.value;
  const move = (ev: PointerEvent) => {
    w.value = Math.min(400, Math.max(160, startW + (ev.clientX - startX)));
  };
  const up = () => {
    window.removeEventListener('pointermove', move);
    window.removeEventListener('pointerup', up);
    onDragEnd();
  };
  window.addEventListener('pointermove', move);
  window.addEventListener('pointerup', up);
}

function onSplitterKey(e: KeyboardEvent) {
  const step = e.shiftKey ? 32 : 8;
  if (e.key === 'ArrowLeft') {
    e.preventDefault();
    w.value = Math.max(160, w.value - step);
  } else if (e.key === 'ArrowRight') {
    e.preventDefault();
    w.value = Math.min(400, w.value + step);
  } else if (e.key === 'Home') {
    e.preventDefault();
    w.value = 160;
  } else if (e.key === 'End') {
    e.preventDefault();
    w.value = 400;
  }
}
</script>

<template>
  <div class="space-y-8">
    <section>
      <h2 class="mb-3 text-lg font-semibold">Splitter（可拖拽 + 键盘可调）</h2>
      <p class="mb-3 text-xs text-fg-muted">
        拖动细线改变宽度；双击复位到 240px；聚焦后用 ← → 调整（Shift 加速）。 D4-6 的 HSplitter
        落地后替换这里。
      </p>

      <div class="flex h-56 overflow-hidden rounded-lg border border-border">
        <div
          class="flex shrink-0 items-center justify-center bg-bg-subtle text-xs text-fg-muted"
          :class="dragging ? '' : 'transition-[width] duration-base ease-out'"
          :style="{ width: `${w}px` }"
        >
          侧栏 {{ w }}px
        </div>

        <div
          class="group relative z-10 -mx-1 flex w-[9px] shrink-0 cursor-col-resize items-stretch justify-center select-none"
          role="separator"
          aria-orientation="vertical"
          :aria-valuenow="w"
          :aria-valuemin="160"
          :aria-valuemax="400"
          tabindex="0"
          @pointerdown="onSplitterDown"
          @dblclick="w = 240"
          @keydown="onSplitterKey"
        >
          <div
            class="w-px transition-colors duration-fast ease-out"
            :class="
              dragging
                ? 'bg-accent/60'
                : 'bg-transparent hover:bg-accent/30 group-hover:bg-border-strong'
            "
          />
        </div>

        <div class="flex min-w-0 flex-1 items-center justify-center bg-bg text-xs text-fg-muted">
          主内容区（自适应）
        </div>
      </div>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">布局尺寸 token</h2>
      <div class="space-y-2 text-xs">
        <div class="flex items-center gap-3">
          <code class="w-56 font-mono">--layout-titlebar-height</code>
          <div class="h-2 rounded bg-accent/40" style="width: var(--layout-titlebar-height)" />
          <span class="text-fg-subtle">40px</span>
        </div>
        <div class="flex items-center gap-3">
          <code class="w-56 font-mono">--layout-sidebar-width</code>
          <div class="h-2 rounded bg-accent/40" style="width: var(--layout-sidebar-width)" />
          <span class="text-fg-subtle">260px</span>
        </div>
        <div class="flex items-center gap-3">
          <code class="w-56 font-mono">--layout-detail-width</code>
          <div class="h-2 rounded bg-accent/40" style="width: var(--layout-detail-width)" />
          <span class="text-fg-subtle">380px</span>
        </div>
        <div class="flex items-center gap-3">
          <code class="w-56 font-mono">--layout-message-max-width</code>
          <div class="h-2 rounded bg-accent/40" style="width: var(--layout-message-max-width)" />
          <span class="text-fg-subtle">820px</span>
        </div>
      </div>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">断点</h2>
      <div class="space-y-1 text-xs text-fg-muted">
        <p>
          <code class="inline-block w-40 font-mono">--bp-compact</code> 900px ——
          低于此值时侧栏自动折叠
        </p>
        <p>
          <code class="inline-block w-40 font-mono">--bp-narrow</code> 700px ——
          低于此值时右栏强制关闭
        </p>
        <p>
          <code class="inline-block w-40 font-mono">--bp-wide</code> 1440px ——
          高于此值时三栏可同时打开
        </p>
      </div>
      <p class="mt-3 text-xs text-fg-subtle">把窗口拖窄到 900px 以下试试：侧栏会自动隐藏。</p>
    </section>
  </div>
</template>
