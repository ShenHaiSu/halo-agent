<!-- src/Web.Client/src/components/__gallery/sections/DisplaySection.vue -->
<!-- D4-9 画廊：展示分区。D4-5 落地后替换为真实 HBadge / HTag 等。 -->
<script setup lang="ts">
const tags = ['全部', '会话', '工具'];
</script>

<template>
  <div class="space-y-8">
    <section>
      <h2 class="mb-3 text-lg font-semibold">Badge</h2>
      <div class="flex flex-wrap items-center gap-4 text-sm">
        <span class="inline-flex items-center gap-1"
          >消息 <span class="rounded-full bg-danger px-1.5 text-2xs text-white">3</span></span
        >
        <span class="inline-flex items-center gap-1"
          >通知 <span class="rounded-full bg-danger px-1.5 text-2xs text-white">99+</span></span
        >
        <span class="inline-flex items-center gap-1"
          >待处理 <span class="rounded-full bg-warning px-1.5 text-2xs text-white">12</span></span
        >
        <span class="inline-flex items-center gap-1"
          >已完成 <span class="rounded-full bg-success px-1.5 text-2xs text-white">5</span></span
        >
        <span class="inline-flex items-center gap-1"
          >新 <span class="h-2 w-2 rounded-full bg-accent"
        /></span>
        <span class="relative inline-flex h-8 w-8 items-center justify-center rounded bg-bg-muted">
          ✉<span class="absolute -right-1 -top-1 rounded-full bg-danger px-1 text-2xs text-white"
            >2</span
          >
        </span>
      </div>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">Tag</h2>
      <div class="flex flex-wrap items-center gap-2">
        <span class="rounded border border-border bg-bg-muted px-2 py-0.5 text-xs">默认</span>
        <span class="rounded bg-accent-subtle px-2 py-0.5 text-xs text-accent">⚡强调</span>
        <span class="rounded bg-success-subtle px-2 py-0.5 text-xs text-success">✓成功</span>
        <span class="rounded bg-warning-subtle px-2 py-0.5 text-xs text-warning">⚠警告</span>
        <span class="rounded bg-danger-subtle px-2 py-0.5 text-xs text-danger">✕危险</span>
        <span class="rounded bg-info-subtle px-2 py-0.5 text-xs text-info">ℹ信息</span>
        <span class="rounded bg-accent-subtle px-2 py-0.5 text-xs text-accent">可关闭 ✕</span>
        <span class="rounded bg-accent-subtle px-1.5 py-px text-2xs text-accent">小号</span>
      </div>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">Divider</h2>
      <div class="max-w-md">
        <p class="text-sm text-fg-muted">上面一段内容</p>
        <hr class="my-3 border-border" />
        <p class="text-sm text-fg-muted">下面一段内容</p>
        <div class="my-3 flex items-center gap-2" role="separator">
          <span class="h-px flex-1 bg-border" /><span class="text-xs text-fg-subtle">分隔文字</span
          ><span class="h-px flex-1 bg-border" />
        </div>
        <p class="text-sm text-fg-muted">再下面一段内容</p>
      </div>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">Empty</h2>
      <div class="grid gap-6 sm:grid-cols-2">
        <div class="rounded-lg border border-border px-4 py-10 text-center">
          <div class="mx-auto mb-2 text-2xl text-fg-subtle" aria-hidden="true">✉</div>
          <p class="text-sm font-medium">还没有会话</p>
          <p class="mt-1 text-xs text-fg-muted">点「新建会话」开始第一次对话。</p>
          <button
            type="button"
            class="mt-3 rounded-md bg-accent px-3 py-1 text-xs font-medium text-accent-fg"
          >
            ＋ 新建会话
          </button>
        </div>
        <div class="rounded-lg border border-border px-4 py-10 text-center">
          <div class="mx-auto mb-2 text-2xl text-fg-subtle" aria-hidden="true">⌕</div>
          <p class="text-sm font-medium">没有匹配结果</p>
          <p class="mt-1 text-xs text-fg-muted">试试换个关键词，或者清空筛选条件。</p>
        </div>
      </div>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">Skeleton</h2>
      <div class="max-w-md space-y-2">
        <div class="flex items-center gap-3">
          <div class="h-9 w-9 animate-pulse rounded-full bg-bg-muted" />
          <div class="flex-1 space-y-2">
            <div class="h-3 animate-pulse rounded bg-bg-muted" />
            <div class="h-3 w-2/3 animate-pulse rounded bg-bg-muted" />
          </div>
        </div>
        <div class="h-16 animate-pulse rounded-md bg-bg-muted" />
        <div class="h-3 rounded bg-bg-muted" />
        <div class="h-3 w-4/5 rounded bg-bg-muted" />
        <p class="text-xs text-fg-subtle">
          上面两组是加载中（animate-pulse），下面两行是静止的（加载完成但内容为空）。
        </p>
      </div>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">Tag 列表（筛选条）示例</h2>
      <div class="flex flex-wrap gap-1.5">
        <button
          v-for="t in tags"
          :key="t"
          type="button"
          class="rounded px-2 py-0.5 text-xs"
          :class="
            t === '全部'
              ? 'bg-accent-subtle text-accent'
              : 'bg-bg-muted text-fg-muted hover:text-fg'
          "
        >
          {{ t }}
        </button>
      </div>
    </section>
  </div>
</template>
