<template>
  <div ref="hostRef" class="flex-1 min-h-0 flex flex-col bg-app overflow-hidden relative" data-testid="workbench-view">
    <MessageTimeline v-if="sub.mode === 'main'" />
    <SubHistoryView v-else />
    <PromptComposer v-if="sub.mode === 'main'" />
    <n-alert v-else type="info" class="mx-4 mb-2 text-xs">子会话历史只读；返回主会话继续对话</n-alert>
    <AskUserModal />
    <RightRailTrigger />
    <RightRailDrawer />
  </div>
</template>
<script setup lang="ts">
import { ref } from 'vue';
import MessageTimeline from '@/components/chat/MessageTimeline.vue';
import PromptComposer from '@/components/chat/PromptComposer.vue';
import AskUserModal from '@/components/chat/AskUserModal.vue';
import SubHistoryView from '@/components/sub/SubHistoryView.vue';
import RightRailTrigger from '@/components/rightRail/RightRailTrigger.vue';
import RightRailDrawer from '@/components/rightRail/RightRailDrawer.vue';
import { createTimelineNav, provideTimelineNav } from '@/composables/useTimelineNav';
import { useSubSessionStore } from '@/stores/subSessionStore';
const sub = useSubSessionStore();
const hostRef = ref<HTMLElement | null>(null);
// need19：TimelineNav 在 WorkbenchView 提供，MessageTimeline/SubHistoryView 注册，HistoryTocPanel 跳转
const nav = createTimelineNav();
provideTimelineNav(nav);
</script>
