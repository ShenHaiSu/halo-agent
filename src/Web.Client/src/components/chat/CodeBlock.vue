<!-- src/Web.Client/src/components/chat/CodeBlock.vue -->
<!-- P5 D5-2 代码块：12 语言三色高亮 + 复制 + 30 行折叠，未闭合围栏显示"生成中"。 -->
<script setup lang="ts">
import { computed, ref } from 'vue';
import { displayLang, resolveLang } from './highlight';
import { tokenize, type Token } from './highlight/tokenizer';
import HIconButton from '@/components/base/HIconButton.vue';
import { useToast } from '@/composables/useToast';
import { copyText } from '@/utils/clipboard';

const props = withDefaults(
  defineProps<{
    code: string;
    lang: string | null;
    /** 围栏是否闭合（未闭合时显示"生成中"，不显示复制按钮，不高亮） */
    closed?: boolean;
  }>(),
  { closed: true }
);

const toast = useToast();
const expanded = ref(false);
const FOLD_AT = 30;

const lines = computed(() => props.code.split('\n'));
const needFold = computed(() => lines.value.length > FOLD_AT);
const shownText = computed(() =>
  needFold.value && !expanded.value ? lines.value.slice(0, FOLD_AT).join('\n') : props.code
);

// 高亮只在"内容或语言变化"时重算（computed 天然做到）。
// 流式进行中（closed === false）不高亮，只显示纯文本。
const highlighted = computed<Token[] | null>(() => {
  if (props.closed === false) return null;
  const def = resolveLang(props.lang);
  if (!def) return null;
  return tokenize(shownText.value, def);
});

async function onCopy(): Promise<void> {
  const ok = await copyText(props.code);
  if (ok) toast.success('已复制代码');
  else toast.error('复制失败，请手动选择');
}
</script>

<template>
  <div class="code-block">
    <div class="code-head">
      <span class="code-lang">{{ displayLang(lang) }}</span>
      <span v-if="closed === false" class="code-status">生成中…</span>
      <span class="code-spacer" />
      <HIconButton v-if="closed !== false" icon="copy" label="复制代码" size="sm" @click="onCopy" />
    </div>

    <pre class="code-pre"><code class="code-inner"><template v-if="highlighted"><span
  v-for="(t, i) in highlighted"
  :key="i"
  :class="t.type === 'plain' ? undefined : `tk-${t.type}`"
>{{ t.value }}</span></template><template v-else>{{ shownText }}</template></code></pre>

    <button v-if="needFold && !expanded" type="button" class="code-more" @click="expanded = true">
      展开全部（共 {{ lines.length }} 行）
    </button>
  </div>
</template>

<style scoped>
.code-block {
  margin: 0 0 var(--space-3);
  border: 1px solid hsl(var(--border));
  border-radius: var(--radius-md);
  overflow: hidden;
  background: hsl(var(--bg-subtle));
}
.code-head {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-1) var(--space-2);
  background: hsl(var(--bg-muted));
  border-bottom: 1px solid hsl(var(--border));
  min-width: 0;
}
.code-lang {
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
  font-family: var(--font-mono);
}
.code-status {
  font-size: var(--text-2xs);
  color: hsl(var(--warning));
}
.code-spacer {
  flex: 1;
  min-width: 0;
}
.code-pre {
  margin: 0;
  padding: var(--space-2) var(--space-3);
  overflow-x: auto;
  font-family: var(--font-mono);
  font-size: var(--text-sm);
  line-height: 1.55;
  tab-size: 2;
}
.code-inner {
  color: hsl(var(--fg));
}
.tk-kw {
  color: hsl(var(--accent));
  font-weight: 500;
}
.tk-str {
  color: hsl(var(--success));
}
.tk-com {
  color: hsl(var(--fg-subtle));
  font-style: italic;
}
.tk-num {
  color: hsl(var(--warning));
}
.tk-punct {
  color: hsl(var(--fg-muted));
}
.code-more {
  width: 100%;
  padding: var(--space-1);
  background: hsl(var(--bg-muted));
  border: 0;
  border-top: 1px solid hsl(var(--border));
  color: hsl(var(--accent));
  font-size: var(--text-xs);
  cursor: pointer;
}
.code-more:hover {
  background: hsl(var(--bg-elevated));
}
</style>
