<!-- src/Web.Client/src/components/__gallery/sections/IconsSection.vue -->
<!-- D4-9 画廊：图标墙。D4-4 落地后数据源切换为 ICONS + HaloIcon。 -->
<script setup lang="ts">
import { computed, ref } from 'vue';

/**
 * 图标墙。
 *
 * ============ 用途 ============
 *  ① 看全部图标，找需要的那个
 *  ② 点一个图标复制它的名字（用于代码）
 *  ③ 调尺寸看清晰度
 * ============ 当前状态 ============
 *  D4-4（67 个内联 SVG + HaloIcon）尚未落地，这里先用名称占位 +
 *  通用几何图形预览搜索/尺寸/复制链路；D4-4 落地后把 ICON_NAMES
 *  换成 `Object.keys(ICONS)`，把占位图形换成 `<HaloIcon :name="n">`。
 * ============ ============
 */
const ICON_NAMES = [
  'plus',
  'minus',
  'x',
  'check',
  'check-circle',
  'alert',
  'alert-circle',
  'info',
  'chevron-right',
  'chevron-left',
  'chevron-up',
  'chevron-down',
  'chevrons-up-down',
  'search',
  'settings',
  'trash',
  'copy',
  'edit',
  'download',
  'upload',
  'refresh',
  'send',
  'message',
  'message-plus',
  'panel-left',
  'monitor',
  'image',
  'zap',
  'palette',
  'filter',
  'more-horizontal',
  'more-vertical',
  'loader',
  'square',
  'folder',
  'file',
  'globe',
  'lock',
  'unlock',
];

const query = ref('');
const size = ref(24);
const copied = ref<string | null>(null);

const filtered = computed(() => {
  const q = query.value.trim().toLowerCase();
  if (!q) return ICON_NAMES;
  return ICON_NAMES.filter((n) => n.includes(q));
});

async function copy(name: string) {
  try {
    await navigator.clipboard.writeText(name);
    copied.value = name;
    window.setTimeout(() => {
      if (copied.value === name) copied.value = null;
    }, 1500);
  } catch {
    /* 忽略 */
  }
}
</script>

<template>
  <div class="space-y-6">
    <div class="flex flex-wrap items-center gap-4">
      <div class="w-64">
        <input
          v-model="query"
          placeholder="搜索图标名…"
          class="w-full rounded-md border border-border bg-bg px-2.5 py-1.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40"
        />
      </div>
      <div class="w-56">
        <label class="block text-xs text-fg-muted">
          尺寸：{{ size }}px
          <input
            v-model.number="size"
            type="range"
            :min="12"
            :max="64"
            :step="2"
            class="w-full accent-[hsl(var(--accent))]"
          />
        </label>
      </div>
      <span class="text-xs text-fg-subtle"
        >共 {{ filtered.length }} / {{ ICON_NAMES.length }} 个</span
      >
    </div>

    <hr class="border-border-subtle" />

    <div class="grid grid-cols-2 gap-1.5 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6">
      <button
        v-for="n in filtered"
        :key="n"
        type="button"
        class="flex flex-col items-center gap-1.5 rounded-md border border-transparent px-2 py-3 text-fg-muted transition-colors duration-fast hover:border-border hover:bg-bg-muted hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40"
        :title="`点击复制：${n}`"
        @click="copy(n)"
      >
        <!-- D4-4 占位图形：等 HaloIcon 落地后替换 -->
        <span
          class="flex items-center justify-center rounded-sm border border-current"
          :style="{
            width: `${size}px`,
            height: `${size}px`,
            fontSize: `${Math.max(9, size - 12)}px`,
          }"
          aria-hidden="true"
        >
          ◇
        </span>
        <span class="w-full truncate text-center font-mono text-2xs">
          {{ copied === n ? '已复制' : n }}
        </span>
      </button>
    </div>

    <p class="text-xs text-fg-subtle">
      提示：D4-4 落地后图标名是字面量类型，写错名字时 TypeScript 会直接报错，不需要运行时验证。
    </p>
  </div>
</template>
