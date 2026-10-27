<!-- src/Web.Client/src/components/__gallery/sections/ThemesSection.vue -->
<!-- D4-9 画廊：主题矩阵（并排展示，局部变量覆盖）。 -->
<script setup lang="ts">
import { ref } from 'vue';

/**
 * 主题矩阵：并排展示所有主题在同一组样本上的效果。
 *
 * ============ 为什么用"局部变量覆盖"而不是"全局切换" ============
 *  全局切换只能一次看一个主题。并排展示才能快速发现
 *  "深色主题里某个语义色对比度不够"这类问题。
 *
 *  实现方式：每个卡片是一个 scope 元素，给它单独设 CSS 变量。
 *  因为变量是继承的，卡片内的所有子元素都用卡片的变量。
 * ============ 当前状态 ============
 *  D4-3（builtinThemes token 表）尚未落地，卡片先用 data-theme
 *  属性复用全局主题变量做并排预览；D4-3 落地后 styleFor 改为
 *  按 builtinThemes[id].tokens 生成内联变量（见 02 §11.4 原文）。
 * ============ ============
 */
const themes = [
  { id: 'midnight', name: '午夜' },
  { id: 'aurora', name: '极光' },
  { id: 'dark', name: '深色' },
];

const benchResult = ref('');

function runBench() {
  benchResult.value = '测量中…';
  const root = document.documentElement;
  const prev = root.dataset.theme ?? '';
  const N = 20;
  const ts: number[] = [];
  for (let i = 0; i < N; i++) {
    const t0 = performance.now();
    root.dataset.theme = i % 2 === 0 ? 'midnight' : 'aurora';
    // 强制同步一次样式计算，否则测到的是 0
    void root.offsetHeight;
    ts.push(performance.now() - t0);
  }
  root.dataset.theme = prev;
  ts.sort((a, b) => a - b);
  const p95 = ts[Math.min(ts.length - 1, Math.floor(ts.length * 0.95))] ?? 0;
  const avg = ts.reduce((a, b) => a + b, 0) / ts.length;
  benchResult.value = `20 次切换：平均 ${avg.toFixed(2)}ms，p95 ${p95.toFixed(2)}ms（目标 < 16ms）`;
}
</script>

<template>
  <div>
    <div class="mb-4 flex items-center gap-3">
      <button
        type="button"
        class="rounded-md border border-border bg-bg-elevated px-3 py-1.5 text-sm"
        @click="runBench"
      >
        测量切换性能
      </button>
      <span class="text-xs text-fg-subtle">（20 次切换，看 p95 是否 &lt; 16ms）</span>
    </div>
    <pre v-if="benchResult" class="mb-6 rounded-md bg-bg-muted p-3 text-xs">{{ benchResult }}</pre>

    <div class="grid grid-cols-1 gap-5 lg:grid-cols-3">
      <section
        v-for="t in themes"
        :key="t.id"
        class="overflow-hidden rounded-lg border border-border"
        :data-theme="t.id"
      >
        <!-- 用局部变量/主题属性，所以这个区域内部用 bg-bg / text-fg 都会跟随 t -->
        <div class="bg-bg p-4 text-fg">
          <div class="mb-3 flex items-center justify-between">
            <h3 class="text-base font-semibold">{{ t.name }}</h3>
            <span class="rounded bg-bg-muted px-1.5 py-0.5 font-mono text-2xs text-fg-muted">
              {{ t.id }}
            </span>
          </div>

          <!-- 样本：按钮 -->
          <div class="mb-3 flex flex-wrap gap-1.5">
            <button class="rounded-md bg-accent px-2.5 py-1 text-xs font-medium text-accent-fg">
              主要
            </button>
            <button
              class="rounded-md border border-border bg-bg-elevated px-2.5 py-1 text-xs text-fg"
            >
              次要
            </button>
            <button class="rounded-md bg-danger px-2.5 py-1 text-xs font-medium text-white">
              危险
            </button>
          </div>

          <!-- 样本：各种背景层 -->
          <div class="mb-3 space-y-1">
            <div class="rounded bg-bg-subtle px-2 py-1 text-2xs">bg-subtle</div>
            <div class="rounded bg-bg-muted px-2 py-1 text-2xs">bg-muted</div>
            <div class="rounded border border-border bg-bg-elevated px-2 py-1 text-2xs">
              bg-elevated
            </div>
          </div>

          <!-- 样本：语义色 -->
          <div class="mb-3 grid grid-cols-4 gap-1">
            <div class="rounded bg-success-subtle px-1 py-1 text-center text-2xs text-success">
              成功
            </div>
            <div class="rounded bg-warning-subtle px-1 py-1 text-center text-2xs text-warning">
              警告
            </div>
            <div class="rounded bg-danger-subtle px-1 py-1 text-center text-2xs text-danger">
              危险
            </div>
            <div class="rounded bg-info-subtle px-1 py-1 text-center text-2xs text-info">信息</div>
          </div>

          <!-- 样本：正文字号层级 -->
          <div class="space-y-0.5 border-t border-border-subtle pt-2">
            <div class="text-xs text-fg">正文 text-xs</div>
            <div class="text-sm text-fg-muted">次要 text-sm</div>
            <div class="text-2xs text-fg-subtle">极次要 text-2xs</div>
            <code class="font-mono text-2xs">code sample</code>
          </div>

          <!-- 样本：阴影 -->
          <div class="mt-3 flex gap-2">
            <div class="h-6 flex-1 rounded bg-bg-elevated shadow-sm" />
            <div class="h-6 flex-1 rounded bg-bg-elevated shadow-md" />
            <div class="h-6 flex-1 rounded bg-bg-elevated shadow-lg" />
          </div>
        </div>
      </section>
    </div>
  </div>
</template>
