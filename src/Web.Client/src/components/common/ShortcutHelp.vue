<!-- src/Web.Client/src/components/common/ShortcutHelp.vue -->
<!-- 快捷键帮助（01 §11）：Ctrl+K/N/,//, F5 禁止，Enter/Shift+Enter/Esc/Alt+↑↓。 -->
<script lang="ts">
import { ref } from 'vue';

const helpOpen = ref(false);

/** 跨组件打开快捷键帮助。 */
export function openShortcutHelp(): void {
  helpOpen.value = true;
}
export function toggleShortcutHelp(): void {
  helpOpen.value = !helpOpen.value;
}
</script>

<script setup lang="ts">
import { onBeforeUnmount, onMounted } from 'vue';
import HDialog from '@/components/base/HDialog.vue';

const GROUPS: Array<{ title: string; items: Array<[string, string]> }> = [
  {
    title: '全局',
    items: [
      ['Ctrl+K', '命令面板 / 搜索'],
      ['Ctrl+N', '新建对话'],
      ['Ctrl+,', '打开设置'],
      ['Ctrl+/', '本帮助'],
      ['F5', '禁止刷新（会丢失状态，已拦截）'],
      ['Alt+↑ / Alt+↓', '切换会话'],
      ['Alt+F4', '关闭窗口'],
    ],
  },
  {
    title: '输入框',
    items: [
      ['Enter', '发送 / 排队（生成中）'],
      ['Shift+Enter', '换行'],
      ['Ctrl+Enter', '也发送'],
      ['Esc', '关闭浮层 / 中断生成'],
      ['↑（空输入时）', '取回上一条消息'],
      ['@', '引用文件'],
      ['/（文本开头）', '斜杠命令'],
    ],
  },
  {
    title: '询问',
    items: [
      ['Enter', '允许一次'],
      ['Esc', '拒绝'],
    ],
  },
  {
    title: '编辑器',
    items: [['Ctrl+S', '保存（技能 / 记忆 / 人格 / MCP）']],
  },
];

function onDocKeydown(e: KeyboardEvent): void {
  if (e.isComposing) return;
  const mod = e.ctrlKey || e.metaKey;
  if (mod && e.key === '/') {
    e.preventDefault();
    helpOpen.value = !helpOpen.value;
  }
}

onMounted(() => document.addEventListener('keydown', onDocKeydown, { capture: true }));
onBeforeUnmount(() => document.removeEventListener('keydown', onDocKeydown, { capture: true }));
</script>

<template>
  <HDialog :open="helpOpen" title="快捷键" width="lg" @update:open="helpOpen = $event">
    <div class="help">
      <section v-for="g in GROUPS" :key="g.title" class="help-group" :aria-label="g.title">
        <h4 class="help-title">{{ g.title }}</h4>
        <div v-for="[k, d] in g.items" :key="k" class="help-row">
          <kbd class="kbd">{{ k }}</kbd>
          <span class="help-desc">{{ d }}</span>
        </div>
      </section>
    </div>
  </HDialog>
</template>

<style scoped>
.help {
  display: flex;
  flex-direction: column;
  gap: var(--space-4);
  max-height: 60vh;
  overflow-y: auto;
}
.help-title {
  font-size: var(--text-sm);
  font-weight: 600;
  margin: 0 0 var(--space-2);
}
.help-row {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  padding: 2px 0;
}
.kbd {
  flex: none;
  min-width: 140px;
  font-family: var(--font-mono);
  font-size: var(--text-xs);
  padding: 2px var(--space-2);
  background: hsl(var(--bg-muted));
  border: 1px solid hsl(var(--border));
  border-radius: var(--radius-xs);
  text-align: center;
}
.help-desc {
  font-size: var(--text-sm);
  color: hsl(var(--fg-muted));
}
</style>
