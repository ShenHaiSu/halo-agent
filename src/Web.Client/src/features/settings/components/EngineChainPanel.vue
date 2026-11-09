<template>
  <div class="space-y-3 min-w-0 max-w-full overflow-x-hidden" data-testid="engine-chain-panel">
    <n-card title="引擎链路默认与备选" size="small">
      <div class="flex items-center gap-3">
        <n-switch
          :value="chainOn"
          data-testid="engine-chain-switch"
          aria-label="默认走引擎链路"
          :loading="switching"
          @update:value="onToggle"
        />
        <span class="text-xs">{{ chainOn ? '默认引擎链路（frame）' : '备选直连链路（legacy）' }}</span>
        <n-button size="small" type="warning" ghost data-testid="engine-chain-rollback" :loading="switching" @click="onRollback">
          一键回备选链路
        </n-button>
      </div>
      <p class="mt-2 text-xs text-secondary">
        默认即引擎链路：历史、轮次、工具卡、用量、设置五面默认 frame；批次 A/B/C 默认全开。
        关闭即显式退回备选链路并重载页面；不删除任何备选代码。
      </p>
    </n-card>

    <n-card title="链路自检（system.info 对账）" size="small">
      <div v-if="probing" class="text-xs text-secondary">正在读取宿主能力探针…</div>
      <div v-else-if="probeError" class="space-y-2">
        <n-alert type="warning" class="text-xs">{{ probeError }}</n-alert>
        <n-button size="small" ghost @click="refreshProbe">重新读取</n-button>
      </div>
      <div v-else class="space-y-2" data-testid="engine-chain-batches">
        <div class="flex items-center gap-2">
          <span class="text-xs text-secondary">批次</span>
          <n-tag
            v-for="batch in servedBatches"
            :key="batch"
            :type="probe.batches.includes(batch) ? 'success' : 'warning'"
            size="small"
            :bordered="false"
            class="font-mono"
          >
            {{ batch }}{{ probe.batches.includes(batch) ? '·开' : '·关' }}
          </n-tag>
        </div>
        <div class="flex flex-wrap items-center gap-2 text-xs">
          <span class="text-secondary">frame 默认</span>
          <n-tag :type="probe.frameDefault === true ? 'success' : 'warning'" size="small" :bordered="false">
            {{ probe.frameDefault === null ? '未知' : probe.frameDefault ? 'true' : 'false' }}
          </n-tag>
          <span class="text-secondary">桥协议</span>
          <span class="font-mono">{{ probe.bridgeVersion === null ? '未知' : probe.bridgeVersion }}</span>
          <span class="text-secondary">契约</span>
          <span class="font-mono">{{ probe.contractVersion || '未知' }}</span>
          <n-button size="small" ghost @click="refreshProbe">重新读取</n-button>
        </div>
      </div>
    </n-card>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { BridgeTransport } from '@/transport/bridgeTransport';
import { isNewEngineEnabled } from '@/transport/legacy';
import {
  ENGINE_BATCHES,
  describeChainProbe,
  emptyChainProbe,
  rejoinFrameChain,
  rollbackToLegacyChain,
  type ChainProbe,
} from '@/transport/engineChain';

const servedBatches = [...ENGINE_BATCHES];
const chainOn = ref(true);
const switching = ref(false);
const probing = ref(false);
const probe = ref<ChainProbe>(emptyChainProbe());
const probeError = ref('');

function reloadPage(): void {
  try {
    window.location.reload();
  } catch {
    /* host shell without reload: the persisted switch still applies on next start */
  }
}

function onToggle(next: boolean): void {
  if (switching.value) return;
  switching.value = true;
  try {
    chainOn.value = next;
    if (next) rejoinFrameChain(reloadPage);
    else rollbackToLegacyChain(reloadPage);
  } finally {
    switching.value = false;
  }
}

function onRollback(): void {
  if (switching.value) return;
  switching.value = true;
  try {
    chainOn.value = false;
    rollbackToLegacyChain(reloadPage);
  } finally {
    switching.value = false;
  }
}

async function refreshProbe(): Promise<void> {
  probing.value = true;
  probeError.value = '';
  try {
    const reply = await new BridgeTransport().invoke('system.info', {});
    if (!reply.ok) {
      probeError.value = `探针不可用（${reply.error?.code || 'UNKNOWN'}）：仍可按开关默认值运行。`;
      return;
    }
    const result = reply.result as Record<string, unknown> | undefined;
    if (!result) {
      probeError.value = '探针返回为空：仍可按开关默认值运行。';
      return;
    }
    probe.value = describeChainProbe(result);
  } catch (err) {
    probeError.value = `探针读取失败：${err instanceof Error ? err.message : String(err)}`;
  } finally {
    probing.value = false;
  }
}

onMounted(() => {
  try {
    chainOn.value = isNewEngineEnabled();
  } catch {
    chainOn.value = true;
  }
  void refreshProbe();
});
</script>
