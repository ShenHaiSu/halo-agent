<!-- src/Web.Client/src/components/__gallery/sections/NavigationSection.vue -->
<!-- D4-9 画廊：导航分区。D4-5 落地后替换为真实 HTabs / HMenu。 -->
<script setup lang="ts">
import { ref } from 'vue';

const tab1 = ref('a');
const tab2 = ref('a');
const tab3 = ref('a');
const menuOpen = ref(false);

const items = [
  { key: 'a', label: '会话' },
  { key: 'b', label: '工具', badge: 3 },
  { key: 'c', label: '文件' },
  { key: 'd', label: '已禁用', disabled: true },
];
</script>

<template>
  <div class="space-y-8">
    <section>
      <h2 class="mb-3 text-lg font-semibold">Tabs · underline（页面级）</h2>
      <div class="flex gap-1 border-b border-border" role="tablist">
        <button
          v-for="t in items"
          :key="t.key"
          type="button"
          role="tab"
          :aria-selected="tab1 === t.key"
          :disabled="t.disabled"
          class="relative px-3 py-1.5 text-sm disabled:cursor-not-allowed disabled:opacity-40"
          :class="tab1 === t.key ? 'font-medium text-fg' : 'text-fg-muted hover:text-fg'"
          @click="tab1 = t.key"
          @keydown.left="tab1 = 'a'"
        >
          {{ t.label }}
          <span v-if="t.badge" class="ml-1 rounded-full bg-danger px-1.5 text-2xs text-white">{{
            t.badge
          }}</span>
          <span
            v-if="tab1 === t.key"
            class="absolute inset-x-2 -bottom-px h-0.5 rounded-full bg-accent"
            aria-hidden="true"
          />
        </button>
      </div>
      <p class="mt-2 text-xs text-fg-subtle">当前：{{ tab1 }}（用 ← → Home End 键切换）</p>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">Tabs · pill（区块级）</h2>
      <div class="flex gap-1 rounded-lg bg-bg-muted p-1" role="tablist">
        <button
          v-for="t in items"
          :key="t.key"
          type="button"
          role="tab"
          :aria-selected="tab2 === t.key"
          :disabled="t.disabled"
          class="rounded-md px-3 py-1 text-sm disabled:cursor-not-allowed disabled:opacity-40"
          :class="
            tab2 === t.key
              ? 'bg-bg-elevated font-medium text-fg shadow-sm'
              : 'text-fg-muted hover:text-fg'
          "
          @click="tab2 = t.key"
        >
          {{ t.label }}
        </button>
      </div>
      <p class="mt-2 text-xs text-fg-subtle">当前：{{ tab2 }}</p>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">Tabs · card（并列视图）</h2>
      <div class="flex gap-2" role="tablist">
        <button
          v-for="t in items"
          :key="t.key"
          type="button"
          role="tab"
          :aria-selected="tab3 === t.key"
          :disabled="t.disabled"
          class="rounded-md border px-3 py-1.5 text-sm disabled:cursor-not-allowed disabled:opacity-40"
          :class="
            tab3 === t.key
              ? 'border-accent bg-accent-subtle text-accent'
              : 'border-border text-fg-muted'
          "
          @click="tab3 = t.key"
        >
          {{ t.label }}
        </button>
      </div>
      <p class="mt-2 text-xs text-fg-subtle">当前：{{ tab3 }}</p>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">Menu</h2>
      <div class="relative inline-block">
        <button
          type="button"
          class="rounded-md border border-border bg-bg-elevated px-3 py-1.5 text-sm"
          aria-haspopup="menu"
          :aria-expanded="menuOpen"
          @click="menuOpen = !menuOpen"
        >
          更多操作
        </button>
        <div
          v-if="menuOpen"
          class="absolute left-0 top-full z-20 mt-1 min-w-52 rounded-md border border-border bg-bg-elevated py-1 shadow-md"
          role="menu"
        >
          <button
            type="button"
            role="menuitem"
            class="flex w-full items-center justify-between px-3 py-1.5 text-left text-sm hover:bg-bg-muted"
            @click="menuOpen = false"
          >
            复制 <kbd class="font-mono text-2xs text-fg-subtle">Ctrl+C</kbd>
          </button>
          <button
            type="button"
            role="menuitem"
            class="flex w-full items-center justify-between px-3 py-1.5 text-left text-sm hover:bg-bg-muted"
            @click="menuOpen = false"
          >
            重命名 <kbd class="font-mono text-2xs text-fg-subtle">F2</kbd>
          </button>
          <button
            type="button"
            role="menuitem"
            class="flex w-full px-3 py-1.5 text-left text-sm hover:bg-bg-muted"
            @click="menuOpen = false"
          >
            导出
          </button>
          <button
            type="button"
            role="menuitem"
            class="flex w-full px-3 py-1.5 text-left text-sm opacity-40"
            disabled
          >
            设置（禁用）
          </button>
          <div class="my-1 h-px bg-border" role="separator" />
          <button
            type="button"
            role="menuitem"
            class="flex w-full items-center justify-between px-3 py-1.5 text-left text-sm text-danger hover:bg-danger-subtle"
            @click="menuOpen = false"
          >
            删除 <kbd class="font-mono text-2xs">Del</kbd>
          </button>
        </div>
      </div>
      <p class="mt-2 text-xs text-fg-subtle">
        验证键盘：打开后 ↓ 进入第一项，↑↓ 移动，Home/End 跳首尾，ESC 关闭。
      </p>
    </section>
  </div>
</template>
