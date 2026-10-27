<!-- src/Web.Client/src/components/common/UpdateBanner.vue -->
<!-- 更新提示（05 §9.2）：P5 只 check 不 install；有新版显示横幅，点查看走外部浏览器。 -->
<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { transport } from '@/transport';
import { hostBridge } from '@/bridge/host';
import { useToast } from '@/composables/useToast';
import HButton from '@/components/base/HButton.vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';

const toast = useToast();

const visible = ref(false);
const latest = ref<string | null>(null);
const notes = ref<string | null>(null);
const checking = ref(false);

async function check(): Promise<void> {
  if (checking.value) return;
  checking.value = true;
  try {
    const r = (await transport.request('update.check', null)) as {
      available?: boolean;
      latestVersion?: string | null;
      notes?: string;
    };
    if (r.available === true && r.latestVersion) {
      latest.value = r.latestVersion;
      notes.value = typeof r.notes === 'string' ? r.notes : null;
      visible.value = true;
    }
  } catch (e) {
    // 检查更新失败静默（不打扰用户）
    console.warn('[update] 检查失败', (e as Error).message);
  } finally {
    checking.value = false;
  }
}

async function openDownload(): Promise<void> {
  const url = (import.meta.env['VITE_DOWNLOAD_URL'] as string | undefined) ?? '';
  if (!url) {
    toast.info(`新版本 ${latest.value ?? ''} 可用，请从发布页下载`);
    return;
  }
  try {
    await hostBridge.openExternal(url);
  } catch (e) {
    toast.error(`打开失败：${(e as Error).message}`);
  }
}

function dismiss(): void {
  visible.value = false;
}

defineExpose({ check });

onMounted(() => {
  // 启动 3 秒后检查一次，不阻塞首屏
  window.setTimeout(() => void check(), 3000);
});
</script>

<template>
  <div v-if="visible" class="update-banner" role="status">
    <HaloIcon name="download" :size="14" />
    <span class="ub-text">
      有新版本 <strong>{{ latest }}</strong> 可用
      <span v-if="notes" class="ub-notes" :title="notes">— {{ notes }}</span>
    </span>
    <span class="spacer" />
    <HButton size="sm" variant="primary" @click="openDownload">查看</HButton>
    <button class="ub-x" type="button" aria-label="稍后再说" @click="dismiss">
      <HaloIcon name="x" :size="12" />
    </button>
  </div>
</template>

<style scoped>
.update-banner {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-1) var(--space-3);
  background: hsl(var(--accent-subtle));
  color: hsl(var(--accent));
  font-size: var(--text-xs);
  flex: none;
  min-width: 0;
}
.ub-text {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.ub-notes {
  color: hsl(var(--fg-muted));
}
.spacer {
  flex: 1;
  min-width: 0;
}
.ub-x {
  background: transparent;
  border: 0;
  cursor: pointer;
  color: inherit;
  opacity: 0.6;
  padding: 0 2px;
}
.ub-x:hover {
  opacity: 1;
}
</style>
