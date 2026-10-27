<!-- src/Web.Client/src/components/__gallery/sections/ButtonsSection.vue -->
<!-- D4-9 画廊：按钮分区。D4-5 落地后替换为真实 HButton / HIconButton。 -->
<script setup lang="ts">
import { ref } from 'vue';

const clicked = ref(0);
const loading = ref(false);

function onClick() {
  clicked.value++;
}

async function demoLoading() {
  loading.value = true;
  await new Promise((r) => setTimeout(r, 1500));
  loading.value = false;
}

const base =
  'inline-flex items-center justify-center gap-1.5 rounded-md px-3 py-1.5 text-sm font-medium transition-colors duration-fast focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40 disabled:cursor-not-allowed disabled:opacity-50';
</script>

<template>
  <div class="space-y-8">
    <section>
      <h2 class="mb-3 text-lg font-semibold">变体（variant）</h2>
      <div class="flex flex-wrap items-center gap-2">
        <button type="button" :class="base" class="bg-accent text-accent-fg hover:bg-accent-hover">
          主要
        </button>
        <button
          type="button"
          :class="base"
          class="border border-border bg-bg-elevated text-fg hover:border-border-strong"
        >
          次要
        </button>
        <button type="button" :class="base" class="text-fg-muted hover:bg-bg-muted hover:text-fg">
          幽灵
        </button>
        <button type="button" :class="base" class="bg-danger text-white hover:opacity-90">
          危险
        </button>
      </div>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">尺寸（size）</h2>
      <div class="flex flex-wrap items-end gap-2">
        <button
          type="button"
          class="rounded-md bg-accent px-2 py-0.5 text-xs font-medium text-accent-fg"
        >
          小 sm
        </button>
        <button type="button" :class="base" class="bg-accent text-accent-fg">中 md</button>
        <button
          type="button"
          class="rounded-md bg-accent px-4 py-2.5 text-base font-medium text-accent-fg"
        >
          大 lg
        </button>
      </div>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">五种状态</h2>
      <div class="flex flex-wrap items-center gap-2">
        <button type="button" :class="base" class="bg-accent text-accent-fg">默认</button>
        <button type="button" :class="base" class="bg-accent text-accent-fg" disabled>禁用</button>
        <button
          type="button"
          :class="base"
          class="relative bg-accent text-accent-fg"
          :disabled="true"
        >
          <span class="opacity-0">处理中</span>
          <span class="absolute inset-0 flex items-center justify-center" aria-hidden="true">
            <span
              class="h-4 w-4 animate-spin rounded-full border-2 border-accent-fg/40 border-t-accent-fg"
            />
          </span>
        </button>
        <span class="text-xs text-fg-subtle"
          >（hover / focus 请自己把鼠标移上去、用 Tab 键试）</span
        >
      </div>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">图标按钮</h2>
      <div class="flex flex-wrap items-center gap-2">
        <button
          type="button"
          :class="base"
          class="border border-border bg-bg-elevated"
          aria-label="搜索"
        >
          ⌕ 搜索
        </button>
        <button
          type="button"
          :class="base"
          class="border border-border bg-bg-elevated"
          aria-label="设置"
        >
          ⚙ 设置
        </button>
        <button
          type="button"
          :class="base"
          class="bg-accent-subtle text-accent"
          aria-label="切换侧栏"
          aria-pressed="true"
        >
          ☰
        </button>
        <button type="button" :class="base" class="border border-border" disabled aria-label="刷新">
          ↻
        </button>
      </div>
      <p class="mt-2 text-xs text-fg-subtle">图标按钮必须有 aria-label；第三个是按下态。</p>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">加载时宽度不抖动</h2>
      <p class="mb-2 text-xs text-fg-muted">点下面的按钮：文字透明但保留占位，所以宽度不变。</p>
      <button
        type="button"
        :class="base"
        class="relative bg-accent text-accent-fg"
        @click="demoLoading"
      >
        <span :class="loading ? 'opacity-0' : ''">下载大文件</span>
        <span
          v-if="loading"
          class="absolute inset-0 flex items-center justify-center"
          aria-hidden="true"
        >
          <span
            class="h-4 w-4 animate-spin rounded-full border-2 border-accent-fg/40 border-t-accent-fg"
          />
        </span>
      </button>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">交互验证</h2>
      <div class="flex items-center gap-3">
        <button
          type="button"
          :class="base"
          class="border border-border bg-bg-elevated"
          @click="onClick"
        >
          点我
        </button>
        <span class="text-sm tabular-nums text-fg-muted">已点击 {{ clicked }} 次</span>
      </div>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">块级（block）</h2>
      <div class="max-w-xs">
        <button type="button" :class="base" class="w-full bg-accent text-accent-fg">
          占满宽度
        </button>
      </div>
    </section>
  </div>
</template>
