<template>
  <div class="flex-1 min-h-0 flex flex-col bg-app overflow-hidden relative" data-testid="workbench-view">
    <MessageTimeline v-if="sub.mode === 'main'" />
    <SubHistoryView v-else />
    <PromptComposer v-if="sub.mode === 'main'" />
    <n-alert v-else type="info" class="mx-4 mb-2 text-xs">子会话历史只读；返回主会话继续对话</n-alert>
    <GoalFloatPanel />
    <AskUserModal />
  </div>
</template>
<script setup lang="ts">
import MessageTimeline from '@/components/chat/MessageTimeline.vue';
import PromptComposer from '@/components/chat/PromptComposer.vue';
import AskUserModal from '@/components/chat/AskUserModal.vue';
import GoalFloatPanel from '@/components/goal/GoalFloatPanel.vue';
import SubHistoryView from '@/components/sub/SubHistoryView.vue';
import { useSubSessionStore } from '@/stores/subSessionStore';
const sub = useSubSessionStore();
</script>
