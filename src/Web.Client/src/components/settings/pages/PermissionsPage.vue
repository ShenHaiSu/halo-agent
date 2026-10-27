<!-- src/Web.Client/src/components/settings/pages/PermissionsPage.vue -->
<!-- 权限分页：3 选 1 HRadio + 危险模式二次确认 HDialog + 沙箱目录只读。 -->
<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { useSessionStore, type PermissionMode } from '@/stores/session';
import { useHostStore } from '@/stores/host';
import { useToast } from '@/composables/useToast';
import SettingRow from '../fields/SettingRow.vue';
import HRadioGroup from '@/components/base/HRadioGroup.vue';
import HRadio from '@/components/base/HRadio.vue';
import HDialog from '@/components/base/HDialog.vue';
import HCheckbox from '@/components/base/HCheckbox.vue';
import HButton from '@/components/base/HButton.vue';
import HInput from '@/components/base/HInput.vue';

const session = useSessionStore();
const host = useHostStore();
const toast = useToast();

const MODES: Array<{ value: PermissionMode; label: string; desc: string }> = [
  { value: 'ask', label: '询问', desc: '每个工具调用都询问你' },
  { value: 'acceptEdits', label: '自动接受编辑', desc: '文件读写自动允许，命令仍需询问' },
  {
    value: 'bypassPermissions',
    label: '完全放行',
    desc: '所有工具调用不再询问，模型可以删除你的文件',
  },
];

const selected = ref<PermissionMode>(session.mode);
const pendingMode = ref<PermissionMode | null>(null);
const ackRisk = ref(false);
const applying = ref(false);

const sandboxDir = computed(() => host.systemInfo?.cwd || session.cwd || '');
const dataDir = computed(() => host.systemInfo?.dataDir || '');

function onSelect(v: string | number): void {
  const next = v as PermissionMode;
  if (next === session.mode) {
    selected.value = session.mode;
    return;
  }
  if (next === 'bypassPermissions') {
    pendingMode.value = next;
    ackRisk.value = false;
    selected.value = session.mode;
    return;
  }
  void apply(next);
}

async function apply(v: PermissionMode): Promise<void> {
  applying.value = true;
  try {
    await session.setMode(v);
    selected.value = session.mode;
    toast.success('权限模式已切换');
  } catch {
    toast.error('切换失败');
    selected.value = session.mode;
  } finally {
    applying.value = false;
  }
}

async function confirmDanger(): Promise<void> {
  if (pendingMode.value === null || !ackRisk.value) return;
  const v = pendingMode.value;
  pendingMode.value = null;
  await apply(v);
}

onMounted(() => {
  selected.value = session.mode;
});

/** 权限模式即时生效（危险模式经确认），无脏态。 */
function isDirty(): boolean {
  return false;
}
function resetToSaved(): void {
  selected.value = session.mode;
}
defineExpose({ isDirty, resetToSaved });
</script>

<template>
  <div class="page">
    <h2 class="page-title">权限</h2>

    <SettingRow label="权限模式" desc="决定工具调用是否需要你确认（permission mode）">
      <HRadioGroup
        :model-value="selected"
        name="perm-mode"
        direction="vertical"
        :disabled="applying"
        @update:model-value="onSelect"
      >
        <HRadio v-for="m in MODES" :key="m.value" :value="m.value">
          <span class="mode-label">{{ m.label }}</span>
          <span class="mode-desc">{{ m.desc }}</span>
        </HRadio>
      </HRadioGroup>
    </SettingRow>

    <SettingRow label="沙箱目录" desc="只读。工具默认只能在这个目录内读写">
      <HInput :model-value="sandboxDir || '（未知）'" readonly />
    </SettingRow>

    <SettingRow label="数据目录" desc="只读。配置与资源文件的存放位置">
      <HInput :model-value="dataDir || '（未知）'" readonly />
    </SettingRow>

    <HDialog
      :open="pendingMode !== null"
      title="确认切换到完全放行？"
      width="sm"
      @update:open="pendingMode = null"
    >
      <p class="danger-p">在「完全放行」模式下，所有工具调用<strong>不再询问</strong>。</p>
      <p class="danger-p danger-strong">模型可以读写、删除你的文件，也可以执行任意命令。</p>
      <label class="ack-row">
        <HCheckbox v-model="ackRisk" />
        <span>我已了解风险，并确认当前工作目录中没有不可恢复的数据</span>
      </label>
      <template #footer>
        <HButton variant="secondary" size="sm" @click="pendingMode = null">取消</HButton>
        <HButton variant="danger" size="sm" :disabled="!ackRisk" @click="confirmDanger"
          >切换到该模式</HButton
        >
      </template>
    </HDialog>
  </div>
</template>

<style scoped>
.page {
  max-width: 760px;
}
.page-title {
  font-size: var(--text-lg);
  font-weight: 600;
  margin: 0 0 var(--space-4);
}
.mode-label {
  font-size: var(--text-sm);
  color: hsl(var(--fg));
  font-weight: 500;
}
.mode-desc {
  display: block;
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
}
.danger-p {
  margin: 0 0 var(--space-2);
  font-size: var(--text-sm);
  line-height: 1.6;
}
.danger-strong {
  color: hsl(var(--danger));
  font-weight: 600;
}
.ack-row {
  display: flex;
  align-items: flex-start;
  gap: var(--space-2);
  font-size: var(--text-sm);
  cursor: pointer;
}
</style>
