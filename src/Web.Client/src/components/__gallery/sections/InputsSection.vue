<!-- src/Web.Client/src/components/__gallery/sections/InputsSection.vue -->
<!-- D4-9 画廊：输入分区。D4-5 落地后替换为真实 HInput 等组件。 -->
<script setup lang="ts">
import { ref } from 'vue';

const text = ref('');
const withLimit = ref('');
const sw = ref(true);
const cb = ref(true);
const radio = ref<string>('a');
const slider = ref(50);
const select = ref('deepseek');

 const inputCls =
   'w-full rounded-md border-2 border-border-strong bg-bg px-2.5 py-1.5 text-sm text-fg placeholder:text-fg-subtle hover:border-fg-subtle focus-visible:border-accent focus-visible:outline-none disabled:cursor-not-allowed disabled:border-border-subtle disabled:opacity-60';
</script>

<template>
  <div class="space-y-8">
    <section>
      <h2 class="mb-3 text-lg font-semibold">单行输入</h2>
      <div class="grid max-w-2xl gap-3">
        <input v-model="text" :class="inputCls" placeholder="普通输入…" />
        <div>
          <input
            v-model="withLimit"
            :class="inputCls"
            placeholder="带计数（最多 20 字）"
            :maxlength="20"
          />
          <p class="mt-1 text-right font-mono text-2xs text-fg-subtle">
            {{ withLimit.length }} / 20
          </p>
        </div>
        <input
          value="有错误的输入"
          :class="inputCls"
          class="border-danger"
          aria-invalid="true"
          placeholder="错误态"
        />
        <input :class="inputCls" placeholder="禁用" disabled />
        <input :class="inputCls" value="只读内容" readonly />
        <input v-model="text" type="search" :class="inputCls" placeholder="可清除（搜索类型）" />
      </div>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">多行输入</h2>
      <div class="grid max-w-2xl gap-3">
        <textarea :class="inputCls" rows="3" placeholder="固定高度…" />
        <textarea :class="inputCls" rows="4" placeholder="自动增高" value="试试按回车换行。" />
      </div>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">开关与勾选</h2>
      <div class="space-y-3">
        <label class="flex cursor-pointer items-center gap-2 text-sm">
          <input v-model="sw" type="checkbox" class="h-4 w-4 accent-[hsl(var(--accent))]" />
          启用某功能（当前：{{ sw ? '开' : '关' }}）
        </label>
        <label class="flex items-center gap-2 text-sm opacity-50">
          <input type="checkbox" class="h-4 w-4" disabled /> 禁用的开关
        </label>
        <label class="flex cursor-pointer items-center gap-2 text-sm">
          <input v-model="cb" type="checkbox" class="h-4 w-4 accent-[hsl(var(--accent))]" />
          普通复选框
        </label>
      </div>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">单选</h2>
      <div class="flex gap-4" role="radiogroup" aria-label="示例单选">
        <label class="flex cursor-pointer items-center gap-1.5 text-sm">
          <input
            v-model="radio"
            type="radio"
            value="a"
            name="gallery-radio"
            class="accent-[hsl(var(--accent))]"
          />
          选项 A
        </label>
        <label class="flex cursor-pointer items-center gap-1.5 text-sm">
          <input
            v-model="radio"
            type="radio"
            value="b"
            name="gallery-radio"
            class="accent-[hsl(var(--accent))]"
          />
          选项 B
        </label>
        <label class="flex items-center gap-1.5 text-sm opacity-50">
          <input type="radio" value="c" name="gallery-radio" disabled /> 选项 C（禁用）
        </label>
      </div>
      <p class="mt-2 text-xs text-fg-subtle">当前选择：{{ radio }}</p>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">滑块</h2>
      <div class="max-w-md space-y-3">
        <label class="block text-sm">
          <span class="mb-1 block text-fg-muted">音量：{{ slider }}</span>
          <input
            v-model.number="slider"
            type="range"
            min="0"
            max="100"
            class="w-full accent-[hsl(var(--accent))]"
          />
        </label>
        <label class="block text-sm opacity-50">
          <span class="mb-1 block text-fg-muted">禁用：30</span>
          <input type="range" :value="30" disabled class="w-full" />
        </label>
      </div>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">下拉（原生 select）</h2>
      <div class="max-w-xs space-y-3">
        <select v-model="select" :class="inputCls">
          <option value="ollama">Ollama（本地）</option>
          <option value="dashscope">阿里云百炼</option>
          <option value="deepseek">DeepSeek</option>
          <option value="openrouter">OpenRouter</option>
          <option value="zai">Z.AI</option>
        </select>
        <p class="text-xs text-fg-subtle">当前：{{ select }}</p>
      </div>
    </section>
  </div>
</template>
