<template>
  <div class="py-10 text-center space-y-2">
    <div class="flex justify-center">
      <Bot class="w-8 h-8 text-accent" />
    </div>
    <p class="text-sm text-primary font-medium">在下方输入任务指令，Agent 将自动编排工具完成</p>
    <p class="text-xs text-secondary font-mono">默认 Runner：CMD 优先 / PYTHON 次之 · 禁用 PowerShell 专属语法</p>
  </div>
</template>

<script setup lang="ts">
import { Bot } from 'lucide-vue-next';
</script>
