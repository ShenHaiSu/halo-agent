<template>
  <!-- Stage 06 Agent 主工作台：消息时间轴 + 底部输入舱（TopBar / Sidebar / StatusBar 由 App.vue 承载，保持兼容） -->
  <div class="flex-1 min-h-0 flex flex-col bg-app overflow-hidden" data-testid="workbench-view">
    <MessageTimeline />
    <PromptComposer />
    <!-- need09-06：阻塞式问询弹窗（全局单例，前/后台会话跟随逻辑在内部） -->
    <AskUserModal />
  </div>
</template>

<script setup lang="ts">
import MessageTimeline from '@/components/chat/MessageTimeline.vue';
import PromptComposer from '@/components/chat/PromptComposer.vue';
import AskUserModal from '@/components/chat/AskUserModal.vue';
</script>
