<!-- src/Web.Client/src/components/__gallery/GalleryApp.vue -->
<!-- D4-9 组件画廊：9 分区侧栏切换（DEV-only，见 App.vue 的条件加载）。 -->
<script setup lang="ts">
import { computed, ref } from 'vue';
import { useTheme } from '@/theme/useTheme';
import ButtonsSection from './sections/ButtonsSection.vue';
import InputsSection from './sections/InputsSection.vue';
import FeedbackSection from './sections/FeedbackSection.vue';
import NavigationSection from './sections/NavigationSection.vue';
import DisplaySection from './sections/DisplaySection.vue';
import IconsSection from './sections/IconsSection.vue';
import ThemesSection from './sections/ThemesSection.vue';
import TokensSection from './sections/TokensSection.vue';
import LayoutSection from './sections/LayoutSection.vue';

/**
 * 组件画廊（活文档）。
 *
 * ============ 为什么画廊是"第一优先"交付物 ============
 *  ① 所有组件的所有状态可视，是验收基座组件的最快方式
 *  ② 所有主题并排对比，是验收主题系统的最快方式
 *  ③ P5 开发的参考：要用按钮时先来这里看有哪些变体
 * ============ 说明 ============
 *  各分区目前用原生 HTML + 语义 token 实现交互与样式；
 *  D4-5 基座组件落地后，逐分区替换为真实 H* 组件
 *  （分区结构与本文件的切换逻辑不需要动）。
 * ============ ============
 */
const sections = [
  { id: 'buttons', label: '按钮' },
  { id: 'inputs', label: '输入' },
  { id: 'feedback', label: '反馈' },
  { id: 'navigation', label: '导航' },
  { id: 'display', label: '展示' },
  { id: 'icons', label: '图标' },
  { id: 'themes', label: '主题' },
  { id: 'tokens', label: 'Token' },
  { id: 'layout', label: '布局' },
] as const;

type SectionId = (typeof sections)[number]['id'];

const active = ref<SectionId>('buttons');
const theme = useTheme();

const modeOptions = computed(() => [
  { value: 'light', label: '浅色' },
  { value: 'dark', label: '深色' },
  { value: 'midnight', label: '午夜' },
  { value: 'system', label: '跟随系统' },
]);

function setMode(v: string) {
  theme.setMode(v);
}

const sectionComponents: Record<SectionId, unknown> = {
  buttons: ButtonsSection,
  inputs: InputsSection,
  feedback: FeedbackSection,
  navigation: NavigationSection,
  display: DisplaySection,
  icons: IconsSection,
  themes: ThemesSection,
  tokens: TokensSection,
  layout: LayoutSection,
};

const activeTitle = computed(() => sections.find((s) => s.id === active.value)?.label ?? '');
</script>

<template>
  <div class="flex h-full w-full overflow-hidden bg-bg text-fg">
    <!-- 画廊侧栏 -->
    <aside class="flex w-56 shrink-0 flex-col border-r border-border bg-bg-subtle">
      <div class="flex items-center gap-2 border-b border-border px-4 py-3">
        <span class="inline-block h-4 w-4 rounded-sm bg-accent" aria-hidden="true" />
        <span class="text-sm font-semibold">组件画廊</span>
      </div>

      <nav class="flex-1 overflow-y-auto p-2">
        <button
          v-for="s in sections"
          :key="s.id"
          type="button"
          class="flex w-full items-center gap-2 rounded-md px-2.5 py-1.5 text-left text-sm transition-colors duration-fast focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40"
          :class="
            active === s.id
              ? 'bg-accent-subtle font-medium text-accent'
              : 'text-fg-muted hover:bg-bg-muted hover:text-fg'
          "
          @click="active = s.id"
        >
          {{ s.label }}
        </button>
      </nav>

      <div class="border-t border-border p-3">
        <div class="mb-2 text-xs text-fg-subtle">主题</div>
        <div class="flex flex-wrap gap-1.5">
          <button
            v-for="o in modeOptions"
            :key="o.value"
            type="button"
            class="rounded-md border px-2 py-1 text-xs transition-colors duration-fast"
            :class="
              theme.mode.value === o.value
                ? 'border-accent bg-accent font-medium text-accent-fg'
                : 'border-border bg-bg-elevated text-fg-muted hover:text-fg'
            "
            @click="setMode(o.value)"
          >
            {{ o.label }}
          </button>
        </div>
        <div v-if="theme.error.value" class="mt-2 text-xs text-danger">
          {{ theme.error.value }}
        </div>
      </div>
    </aside>

    <!-- 内容 -->
    <main class="min-w-0 flex-1 overflow-y-auto p-6">
      <h1 class="mb-6 text-2xl font-semibold">{{ activeTitle }}</h1>
      <component :is="sectionComponents[active]" />
    </main>
  </div>
</template>
