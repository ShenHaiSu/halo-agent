<template>
  <n-drawer
    v-model:show="ui.rightRailOpen"
    placement="right"
    :width="ui.rightRailWidth"
    :min-width="320"
    :max-width="720"
    resizable
    to="body"
    :mask-closable="false"
    :auto-focus="false"
    :trap-focus="false"
    :block-scroll="false"
    :native-scrollbar="false"
    :close-on-esc="true"
    show-mask="transparent"
    content-class="right-rail-drawer"
    content-style="height:100%;min-height:0;display:flex;flex-direction:column;"
    :style="{ maxWidth: 'min(720px, 86vw)' }"
    @update:width="onWidth"
  >
      <n-drawer-content
        :title="null"
        closable
        :native-scrollbar="false"
        body-content-style="padding:0;height:100%;min-height:0;display:flex;flex-direction:column;"
        class="right-rail-drawer-content"
      >
        <div class="right-rail-root" data-testid="right-rail-drawer">
          <nav class="right-rail-tabs" aria-label="右侧栏切换">
            <n-tooltip v-for="t in tabs" :key="t.key" placement="left" :delay="400">
              <template #trigger>
                <button
                  class="rail-tab"
                  :class="{ 'is-active': ui.rightRailTab === t.key }"
                  :data-testid="`rail-tab-${t.key}`"
                  :aria-current="ui.rightRailTab === t.key ? 'page' : undefined"
                  :aria-label="t.label"
                  @click="ui.rightRailTab = t.key"
                >
                  <component :is="t.icon" class="w-[18px] h-[18px]" />
                  <span v-if="t.badge" class="rail-badge">{{ t.badge }}</span>
                </button>
              </template>
              {{ t.label }}
            </n-tooltip>
          </nav>
          <section class="right-rail-content">
            <PanelBoundary>
              <GoalPanelContent v-if="ui.rightRailTab === 'goal'" mode="drawer" :readonly="isSubReadonly" />
              <HistoryTocPanel v-else />
            </PanelBoundary>
          </section>
        </div>
      </n-drawer-content>
    </n-drawer>
</template>
<script setup lang="ts">
/**
 * need20：Drawer 外壳 + icon rail + 内容路由 + watchSession 唯一挂载点。
 * 遮罩透明 + mask-closable=false：点击外部不关闭，只能右上角 X / 触发按钮 / Esc 收起。
 */
import { computed, onMounted, watch } from 'vue';
import { Target, ListTree } from 'lucide-vue-next';
import { useUiStore } from '@/stores/uiStore';
import { useGoalStore } from '@/stores/goalStore';
import { useSubSessionStore } from '@/stores/subSessionStore';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import GoalPanelContent from '@/components/goal/GoalPanelContent.vue';
import HistoryTocPanel from './HistoryTocPanel.vue';
import PanelBoundary from './PanelBoundary.vue';

const ui = useUiStore();
const goalStore = useGoalStore();
const sub = useSubSessionStore();
const ws = useWorkspaceStore();

const isSubReadonly = computed(() => sub.mode === 'sub');
const tabs = computed(() => [
  { key: 'goal' as const, label: `目标${goalStore.pct ? ` · ${goalStore.pct}%` : ''}`, icon: Target, badge: '' },
  { key: 'toc' as const, label: '历史目录', icon: ListTree, badge: '' },
]);

function onWidth(w: number): void {
  ui.setWidth(w);
}

onMounted(() => {
  goalStore.watchSession();
});

watch(
  () => [ui.rightRailOpen, ui.rightRailTab] as const,
  ([open, tab]) => {
    if (open && tab === 'goal') {
      const wsId = ws.activeWorkspaceId || '';
      const sessId = ws.activeSessionId || '';
      if (wsId && sessId) void goalStore.load(wsId, sessId);
    }
  }
);
</script>
<style scoped>
.right-rail-root { display: flex; flex: 1 1 auto; height: 100%; min-height: 0; max-height: 100%; }
.right-rail-tabs {
  width: 48px; flex: 0 0 48px; display: flex; flex-direction: column; gap: 6px;
  padding: 10px 6px; background: var(--bg-sidebar); border-right: 1px solid var(--border-subtle);
}
.right-rail-content { flex: 1 1 auto; min-width: 0; display: flex; flex-direction: column; min-height: 0; padding: 10px 10px 8px; overflow: hidden auto; }
.rail-badge {
  position: absolute; top: 2px; right: 2px; min-width: 14px; height: 14px; padding: 0 3px;
  border-radius: 7px; background: var(--accent-primary); color: #fff;
  font-size: 10px; line-height: 14px; text-align: center;
}
/* need20 S2.3：rail 按钮强制正方形，避免被 flex 压扁 */
.right-rail-tabs .rail-tab { flex: 0 0 36px; width: 36px; height: 36px; margin: 0 auto; }
</style>
