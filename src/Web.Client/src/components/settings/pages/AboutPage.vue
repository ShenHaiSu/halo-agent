<!-- src/Web.Client/src/components/settings/pages/AboutPage.vue -->
<!-- 关于分页：版本号 + 协议版本 / 检查更新 / 诊断信息复制 / 打开日志目录 / 数据目录。 -->
<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { transport } from '@/transport';
import { hostBridge } from '@/bridge/host';
import { useHostStore } from '@/stores/host';
import { useToast } from '@/composables/useToast';
import SettingRow from '../fields/SettingRow.vue';
import HInput from '@/components/base/HInput.vue';
import HButton from '@/components/base/HButton.vue';
import HSpinner from '@/components/base/HSpinner.vue';

const host = useHostStore();
const toast = useToast();

const version = ref('');
const protocol = ref('');
const loading = ref(true);
const checking = ref(false);
const updateInfo = ref<string | null>(null);

const dataDir = ref('');

async function copyText(text: string): Promise<boolean> {
  try {
    await hostBridge.copyText(text);
    return true;
  } catch {
    return false;
  }
}

async function load(): Promise<void> {
  loading.value = true;
  try {
    const r = (await transport.request('system.info', null)) as {
      version?: string;
      protocolVersion?: string;
      protocol?: string;
    };
    version.value = r.version ?? '';
    protocol.value = r.protocolVersion ?? r.protocol ?? '';
  } catch {
    version.value = '';
    protocol.value = '';
  } finally {
    loading.value = false;
  }
}

async function checkUpdate(): Promise<void> {
  if (checking.value) return;
  checking.value = true;
  updateInfo.value = null;
  try {
    const r = (await transport.request('update.check', null)) as {
      available?: boolean;
      latestVersion?: string | null;
      notes?: string;
    };
    if (r.available === true && r.latestVersion) {
      updateInfo.value = `有新版本 ${r.latestVersion} 可用${r.notes ? `：${r.notes}` : ''}（当前为 ${version.value || '未知版本'}）`;
    } else {
      updateInfo.value = '当前已是最新版本。';
    }
  } catch (e) {
    updateInfo.value = `检查更新失败：${(e as Error).message}`;
  } finally {
    checking.value = false;
  }
}

async function copyDiagnostics(): Promise<void> {
  try {
    const d = await transport.request<unknown>('system.diagnostics', null);
    const ok = await copyText(JSON.stringify(d, null, 2));
    toast[ok ? 'success' : 'error'](ok ? '诊断信息已复制' : '复制失败');
  } catch (e) {
    toast.error(`读取诊断信息失败：${(e as Error).message}`);
  }
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
  dataDir.value = host.systemInfo?.dataDir ?? '';
  void load();
});

function isDirty(): boolean {
  return false;
}
function resetToSaved(): void {
  /* 无 */
}
defineExpose({ isDirty, resetToSaved });
</script>

<template>
  <div class="page">
    <h2 class="page-title">关于</h2>

    <div v-if="loading" class="page-state"><HSpinner size="sm" /><span>加载中…</span></div>
    <template v-else>
      <SettingRow label="版本" desc="应用版本号">
        <HInput :model-value="version || '（未知）'" readonly />
      </SettingRow>

      <SettingRow label="协议版本" desc="前后端通信协议版本">
        <HInput :model-value="protocol || '（未知）'" readonly />
      </SettingRow>

      <SettingRow label="检查更新" desc="只检查并提示，不自动下载安装">
        <div class="row-flex">
          <HButton size="sm" variant="secondary" :loading="checking" @click="checkUpdate"
            >检查更新</HButton
          >
          <span v-if="updateInfo" class="update-info" role="status">{{ updateInfo }}</span>
        </div>
      </SettingRow>

      <SettingRow label="诊断信息" desc="复制诊断信息，便于反馈问题">
        <HButton size="sm" variant="secondary" @click="copyDiagnostics">复制诊断信息</HButton>
      </SettingRow>

      <SettingRow label="数据目录" desc="只读。日志文件存放在该目录下">
        <div class="row-flex">
          <HInput :model-value="dataDir || '（未知）'" readonly />
          <HButton size="sm" variant="secondary" :disabled="!dataDir" @click="openDataDir"
            >打开目录</HButton
          >
        </div>
      </SettingRow>
    </template>
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
.page-state {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  color: hsl(var(--fg-muted));
  font-size: var(--text-sm);
  padding: var(--space-4) 0;
}
.row-flex {
  display: flex;
  gap: var(--space-2);
  align-items: center;
  min-width: 0;
}
.update-info {
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
  min-width: 0;
}
</style>
