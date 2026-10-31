<!-- src/Web.Client/src/components/settings/pages/GeneralPage.vue -->
<!-- 常规分页：主题即时保存；语言灰显；数据目录只读+打开；恢复上次会话；关窗行为。 -->
<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { useHostStore } from '@/stores/host';
import { useThemeStore } from '@/stores/theme';
import { hostBridge } from '@/bridge/host';
import { useToast } from '@/composables/useToast';
import SettingRow from '../fields/SettingRow.vue';
import HSelect from '@/components/base/HSelect.vue';
import HInput from '@/components/base/HInput.vue';
import HSwitch from '@/components/base/HSwitch.vue';
import HButton from '@/components/base/HButton.vue';

const host = useHostStore();
const theme = useThemeStore();
const toast = useToast();

const RESTORE_KEY = 'haloagent.restoreLastSession';
const CLOSE_KEY = 'haloagent.closeBehavior';

const restoreLast = ref(true);
const closeBehavior = ref<'minimize' | 'exit'>('minimize');
const dataDir = ref('');

function loadLocal(): void {
  try {
    restoreLast.value = localStorage.getItem(RESTORE_KEY) !== '0';
    closeBehavior.value = localStorage.getItem(CLOSE_KEY) === 'exit' ? 'exit' : 'minimize';
  } catch {
    /* 忽略 */
  }
}

function saveLocal(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    /* 忽略 */
  }
}

function onThemeChange(v: string): void {
  theme.setMode(v);
}

async function openDataDir(): Promise<void> {
  if (!dataDir.value) {
    toast.warning('数据目录未知');
    return;
  }
  try {
    await hostBridge.showItemInFolder(dataDir.value);
  } catch (e) {
    toast.error(`打开失败：${(e as Error).message}`);
  }
}

onMounted(() => {
  loadLocal();
  dataDir.value = host.systemInfo?.dataDir ?? '';
});

/** 常规页即时保存，无脏态。 */
function isDirty(): boolean {
  return false;
}
function resetToSaved(): void {
  loadLocal();
}
defineExpose({ isDirty, resetToSaved });
</script>

<template>
  <div class="page">
    <h2 class="page-title">常规</h2>

    <SettingRow label="主题模式" desc="浅色 / 深色 / 跟随系统 / 午夜，切换后立即生效">
      <HSelect :model-value="String(theme.mode)" @update:model-value="onThemeChange">
        <option value="light">浅色</option>
        <option value="dark">深色</option>
        <option value="system">跟随系统</option>
        <option value="midnight">午夜</option>
      </HSelect>
    </SettingRow>

    <SettingRow label="语言" desc="当前版本仅提供中文">
      <HInput model-value="中文" disabled />
    </SettingRow>

    <SettingRow label="数据目录" desc="只读。存放配置、会话历史与资源文件">
      <div class="dir-row">
        <HInput :model-value="dataDir || '（未知）'" readonly />
        <HButton size="sm" variant="secondary" :disabled="!dataDir" @click="openDataDir"
          >打开</HButton
        >
      </div>
    </SettingRow>

    <SettingRow label="启动时恢复上次会话" desc="打开应用后自动回到上次的会话">
      <HSwitch
        :model-value="restoreLast"
        label="启动时恢复上次会话"
        @update:model-value="
          (v: boolean) => {
            restoreLast = v;
            saveLocal(RESTORE_KEY, v ? '1' : '0');
          }
        "
      />
    </SettingRow>

    <SettingRow label="关闭窗口时" desc="点右上角关闭后的行为（保存在本地）">
      <HSelect
        :model-value="closeBehavior"
        @update:model-value="
          (v: string) => {
            closeBehavior = v === 'exit' ? 'exit' : 'minimize';
            saveLocal(CLOSE_KEY, closeBehavior);
          }
        "
      >
        <option value="minimize">最小化到托盘</option>
        <option value="exit">直接退出</option>
      </HSelect>
    </SettingRow>
  </div>
</template>

<style scoped>
 .page {
   max-width: var(--settings-page-max, 860px);
 }
 .page-title {
   font-size: var(--text-base);
   font-weight: 600;
   margin: 0 0 var(--space-2);
 }
.dir-row {
  display: flex;
  gap: var(--space-2);
  align-items: center;
  min-width: 0;
}
</style>
