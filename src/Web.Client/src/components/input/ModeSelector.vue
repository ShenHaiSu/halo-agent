<!-- src/Web.Client/src/components/input/ModeSelector.vue -->
<!-- 输入框上方权限级别快切触发器 + 弹出面板，仿 ModelSelector 的功能与样式。 -->
<!-- 点选即切换，无二次确认；后端落盘，前端只做即时反馈。 -->
<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue';
import { useSessionStore, type PermissionMode } from '@/stores/session';
import { useToast } from '@/composables/useToast';
import HButton from '@/components/base/HButton.vue';
import HTooltip from '@/components/base/HTooltip.vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import ModeSelectorPanel from './ModeSelectorPanel.vue';
import { PERMISSION_MODES, permissionModeDesc, permissionModeLabel } from './permissionModes';
import { openSettings } from '@/components/settings/SettingsApp.vue';

const session = useSessionStore();
const toast = useToast();

const open = ref(false);
const switching = ref(false);
const triggerRef = ref<HTMLElement | null>(null);

const currentLabel = computed(() => permissionModeLabel(session.mode));
const currentDesc = computed(() => permissionModeDesc(session.mode));
const dotClass = computed(() => {
  const m = PERMISSION_MODES.find((x) => x.value === session.mode);
  if (m?.danger) return 'danger';
  if (m?.warning) return 'warning';
  return '';
});

async function onSelect(mode: PermissionMode): Promise<void> {
  if (switching.value) return;
  open.value = false;
  focusTrigger();
  if (mode === session.mode) return;
  switching.value = true;
  try {
    await session.setMode(mode);
    if (session.mode === mode) toast.success(`已切换权限：${permissionModeLabel(mode)}`);
    else toast.error('切换失败（已回滚）');
  } catch (e) {
    toast.error(`切换失败：${(e as Error).message}`);
  } finally {
    switching.value = false;
  }
}

function toggle(): void {
  if (switching.value) return;
  open.value = !open.value;
  if (!open.value) focusTrigger();
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

function onDocClick(e: MouseEvent): void {
  if (!open.value) return;
  const t = e.target as HTMLElement | null;
  if (t?.closest?.('.composer-permbar')) return;
  open.value = false;
}

function onDocKey(e: KeyboardEvent): void {
  if (e.key === 'Escape' && open.value) {
    e.stopPropagation();
    closePanel();
  }
}

onMounted(() => {
  document.addEventListener('mousedown', onDocClick);
  document.addEventListener('keydown', onDocKey, true);
});

onBeforeUnmount(() => {
  document.removeEventListener('mousedown', onDocClick);
  document.removeEventListener('keydown', onDocKey, true);
});
</script>

<template>
  <div class="composer-permbar">
    <div ref="triggerRef" class="modebar-trigger">
      <HTooltip :content="`${currentLabel}：${currentDesc}`" placement="top" :delay="300">
        <HButton
          size="sm"
          variant="ghost"
          :disabled="switching"
          :aria-haspopup="'listbox'"
          :aria-expanded="open"
          @click="toggle"
        >
          <span class="trigger-dot" :class="dotClass" aria-hidden="true">●</span>
          <span class="trigger-label">{{ currentLabel }}</span>
          <HaloIcon name="chevron-down" :size="14" />
        </HButton>
      </HTooltip>
    </div>
    <div v-if="open" class="modebar-pop">
      <ModeSelectorPanel
        :current="session.mode"
        @select="onSelect"
        @close="closePanel"
        @open-settings="openSettings('permissions')"
      />
    </div>
  </div>
</template>

<style scoped>
.composer-permbar {
  position: relative;
  display: flex;
  align-items: center;
  min-width: 0;
}
.modebar-trigger {
  display: flex;
  align-items: center;
  min-width: 0;
}
.trigger-dot {
  color: hsl(var(--fg-subtle));
  font-size: var(--text-2xs);
}
.trigger-dot.warning {
  color: hsl(var(--warning));
}
.trigger-dot.danger {
  color: hsl(var(--danger));
}
.trigger-label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 160px;
}
.modebar-pop {
  position: absolute;
  left: 0;
  top: auto;
  bottom: calc(100% + 4px);
  z-index: var(--z-popover);
}
</style>
