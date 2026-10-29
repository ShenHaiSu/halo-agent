<!-- src/Web.Client/src/components/input/ModelSelector.vue -->
<!-- 主界面模型选择器触发器 + 拉数 + setActive（P12-01）。挂在 Composer composer-box 之后独立一行。 -->
<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import { useSessionStore } from '@/stores/session';
import { useStreamStore } from '@/stores/stream';
import { usePermissionsStore } from '@/stores/permissions';
import HButton from '@/components/base/HButton.vue';
import HTooltip from '@/components/base/HTooltip.vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import ModelSelectorPanel from './ModelSelectorPanel.vue';
import { normalizeProvidersTree, type ProvidersTree } from '@/components/settings/providerEditing';
import { openSettings } from '@/components/settings/SettingsApp.vue';

const session = useSessionStore();
const stream = useStreamStore();
const perms = usePermissionsStore();
const toast = useToast();

const open = ref(false);
const tree = ref<ProvidersTree>({ activeProvider: '', activeModel: '', providers: [] });
const loaded = ref(false);
const switching = ref(false);
const triggerRef = ref<HTMLElement | null>(null);

const disabled = computed(() => stream.busy || perms.pending !== null || switching.value);
const disabledReason = computed(() => {
  if (stream.busy || perms.pending !== null) return '回合执行中不可切换';
  return '';
});

/** 未配置：configured=false 或空。 */
const unconfigured = computed(
  () => !session.configured || !session.provider || !session.model,
);

const triggerLabel = computed(() => {
  if (unconfigured.value) return '未配置模型 → 去设置';
  const p = session.providerDisplay || session.provider;
  const m = session.modelDisplay || session.model;
  return `${p} / ${m}`;
});

const weakInfo = computed(() => {
  if (unconfigured.value) return '';
  const parts: string[] = [];
  if (session.contextWindow) parts.push(formatCtx(session.contextWindow));
  if (session.protocol) parts.push(session.protocol);
  return parts.join(' · ');
});

const fullIds = computed(() => `${session.provider}/${session.model}`);

function formatCtx(n: number | null): string {
  if (n === null || n === undefined) return '';
  if (n >= 1000000) {
    const m = n / 1000000;
    return `${Number.isInteger(m) ? m : m.toFixed(1)}M`;
  }
  if (n >= 1000) return `${Math.round(n / 1000)}K`;
  return String(n);
}

async function loadTree(): Promise<void> {
  try {
    const s = await transport.request('settings.get', null);
    tree.value = normalizeProvidersTree(s);
    loaded.value = true;
  } catch {
    /* 面板空态兜底 */
  }
}

function toggle(): void {
  if (disabled.value) {
    if (disabledReason.value) toast.warning(disabledReason.value);
    return;
  }
  if (unconfigured.value) {
    openSettings('model');
    return;
  }
  open.value = !open.value;
  if (open.value) {
    void loadTree();
    void nextTick(() => {
      // 面板内 onMounted 自动聚焦搜索框
    });
  } else {
    focusTrigger();
  }
}

function closePanel(): void {
  open.value = false;
  focusTrigger();
}

function focusTrigger(): void {
  void nextTick(() => {
    const el = triggerRef.value?.querySelector('button') as HTMLElement | null;
    el?.focus();
  });
}

async function onSelect(providerId: string, modelId: string): Promise<void> {
  if (switching.value) return;
  open.value = false;
  focusTrigger();
  if (providerId === session.provider && modelId === session.model) return;
  switching.value = true;
  try {
    await transport.request('settings.setActive', { providerId, modelId });
    // 成功后等 state.snapshot 推送刷新高亮（session.ts），此处不另拉 settings.get。
    toast.success(`已切换：${providerId}/${modelId}`);
  } catch (e) {
    // 失败本地回滚：不改本地高亮（高亮源自 snapshot，未动），仅 toast。
    toast.error(`切换失败：${(e as Error).message}`);
  } finally {
    switching.value = false;
  }
}

function onDocClick(e: MouseEvent): void {
  if (!open.value) return;
  const t = e.target as HTMLElement | null;
  if (t?.closest?.('.composer-modelbar')) return;
  open.value = false;
}

function onDocKey(e: KeyboardEvent): void {
  if (e.key === 'Escape' && open.value) {
    e.stopPropagation();
    closePanel();
  }
}

onMounted(() => {
  void loadTree();
  document.addEventListener('mousedown', onDocClick);
  document.addEventListener('keydown', onDocKey, true);
});

onBeforeUnmount(() => {
  document.removeEventListener('mousedown', onDocClick);
  document.removeEventListener('keydown', onDocKey, true);
});
</script>

<template>
  <div class="composer-modelbar">
    <div ref="triggerRef" class="modelbar-trigger">
      <HTooltip :content="unconfigured ? '去模型设置' : fullIds" placement="top" :delay="300">
        <HButton
          size="sm"
          variant="ghost"
          :disabled="disabled && !unconfigured"
          :aria-haspopup="'listbox'"
          :aria-expanded="open"
          @click="toggle"
        >
          <span class="trigger-dot" :class="{ off: unconfigured }" aria-hidden="true">●</span>
          <span class="trigger-label">{{ triggerLabel }}</span>
          <HaloIcon name="chevron-down" :size="14" />
        </HButton>
      </HTooltip>
      <span v-if="weakInfo" class="trigger-weak">{{ weakInfo }}</span>
    </div>
    <div v-if="open" class="modelbar-pop">
      <ModelSelectorPanel
        :groups="tree.providers"
        :active-provider="session.provider"
        :active-model="session.model"
        @select="onSelect"
        @close="closePanel"
        @open-settings="openSettings('model')"
      />
    </div>
  </div>
</template>

<style scoped>
.composer-modelbar {
  position: relative;
  display: flex;
  align-items: center;
  min-width: 0;
  padding: 0 var(--space-1);
}
.modelbar-trigger {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  min-width: 0;
}
.trigger-dot {
  color: hsl(var(--accent));
  font-size: 10px;
}
.trigger-dot.off {
  color: hsl(var(--fg-subtle));
}
.trigger-label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 320px;
}
.trigger-weak {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  font-family: var(--font-mono);
}
.modelbar-pop {
  position: absolute;
  left: 0;
  top: calc(100% + 4px);
  z-index: 40;
}
</style>
