<!-- src/Web.Client/src/components/__gallery/sections/TokensSection.vue -->
<!-- D4-9 画廊：设计 token 可视化（颜色 5 组 + 字号 8 档 + 间距 12 档 + 圆角 6 档 + 阴影 6 档）。 -->
<script setup lang="ts">
/**
 * 设计 token 可视化。
 *
 * ============ 为什么需要这个 ============
 *  改了 token 值以后，需要立刻看到"所有用到它的地方"效果如何。
 *  这个页面按类别列出所有 token，并给出实际渲染样本。
 * ============ ============
 */
const colorGroups = [
  {
    name: '背景',
    tokens: ['bg', 'bg-subtle', 'bg-muted', 'bg-elevated'],
  },
  {
    name: '前景',
    tokens: ['fg', 'fg-muted', 'fg-subtle', 'fg-inverse'],
  },
  {
    name: '边框',
    tokens: ['border', 'border-strong', 'border-subtle'],
  },
  {
    name: '强调色',
    tokens: ['accent', 'accent-hover', 'accent-fg', 'accent-subtle'],
  },
  {
    name: '语义色',
    tokens: [
      'success',
      'success-subtle',
      'warning',
      'warning-subtle',
      'danger',
      'danger-subtle',
      'info',
      'info-subtle',
    ],
  },
];

const fontSizes = [
  { token: 'text-2xs', label: '11px' },
  { token: 'text-xs', label: '12px' },
  { token: 'text-sm', label: '13px' },
  { token: 'text-base', label: '14px（正文）' },
  { token: 'text-lg', label: '16px' },
  { token: 'text-xl', label: '18px' },
  { token: 'text-2xl', label: '22px' },
  { token: 'text-3xl', label: '28px' },
];

const spacings = [0.5, 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 12, 16];
const radii = [
  { token: 'radius-sm', px: '3px' },
  { token: 'radius-base', px: '4px' },
  { token: 'radius-md', px: '6px' },
  { token: 'radius-lg', px: '8px' },
  { token: 'radius-xl', px: '12px' },
  { token: 'radius-2xl', px: '16px' },
];
const shadows = ['shadow-sm', 'shadow-base', 'shadow-md', 'shadow-lg', 'shadow-xl', 'shadow-focus'];

function colorStyle(token: string): Record<string, string> {
  return { background: `hsl(var(--${token}))` };
}
</script>

<template>
  <div class="space-y-8">
    <!-- 颜色 -->
    <section v-for="g in colorGroups" :key="g.name">
      <h2 class="mb-3 text-lg font-semibold">{{ g.name }}</h2>
      <div class="grid grid-cols-2 gap-2 sm:grid-cols-4">
        <div
          v-for="tok in g.tokens"
          :key="tok"
          class="overflow-hidden rounded-md border border-border"
        >
          <div class="h-12" :style="colorStyle(tok)" />
          <div class="bg-bg-subtle px-2 py-1.5">
            <div class="font-mono text-2xs text-fg">--{{ tok }}</div>
            <div class="font-mono text-2xs text-fg-subtle">hsl(var(--{{ tok }}))</div>
          </div>
        </div>
      </div>
    </section>

    <!-- 字号 -->
    <section>
      <h2 class="mb-3 text-lg font-semibold">字号与行高</h2>
      <div class="space-y-2">
        <div
          v-for="f in fontSizes"
          :key="f.token"
          class="flex items-baseline gap-3 border-b border-border-subtle pb-2"
        >
          <span class="w-24 shrink-0 font-mono text-2xs text-fg-subtle">{{ f.token }}</span>
          <span class="w-28 shrink-0 font-mono text-2xs text-fg-subtle">{{ f.label }}</span>
          <span :class="f.token" class="text-fg">
            中文示例 Hello 123 —— 加粗 <strong class="font-semibold">Bold</strong>
          </span>
        </div>
      </div>
    </section>

    <!-- 间距 -->
    <section>
      <h2 class="mb-3 text-lg font-semibold">间距（4px 基准）</h2>
      <div class="space-y-1">
        <div v-for="s in spacings" :key="s" class="flex items-center gap-3">
          <span class="w-16 shrink-0 font-mono text-2xs text-fg-subtle">space-{{ s }}</span>
          <div
            class="h-3 rounded-sm bg-accent/40"
            :style="{ width: `calc(var(--space-${s}) * 4)` }"
          />
          <span class="font-mono text-2xs text-fg-subtle">{{ s * 4 }}px</span>
        </div>
      </div>
    </section>

    <!-- 圆角 -->
    <section>
      <h2 class="mb-3 text-lg font-semibold">圆角</h2>
      <div class="flex flex-wrap gap-4">
        <div v-for="r in radii" :key="r.token" class="text-center">
          <div
            class="h-16 w-16 border border-border-strong bg-bg-muted"
            :style="{ borderRadius: `var(--${r.token})` }"
          />
          <div class="mt-1 font-mono text-2xs text-fg-subtle">{{ r.px }}</div>
        </div>
      </div>
    </section>

    <!-- 阴影 -->
    <section>
      <h2 class="mb-3 text-lg font-semibold">阴影</h2>
      <div class="flex flex-wrap gap-6 p-4">
        <div
          v-for="sh in shadows"
          :key="sh"
          class="flex h-20 w-28 items-center justify-center rounded-lg bg-bg-elevated"
          :style="{ boxShadow: `var(--${sh})` }"
        >
          <span class="font-mono text-2xs text-fg-subtle">{{ sh }}</span>
        </div>
      </div>
    </section>
  </div>
</template>
