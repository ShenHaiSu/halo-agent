<template>
  <div class="space-y-4 min-w-0 max-w-full overflow-x-hidden">
    <n-card title="跨代宿主内核与系统环境遥测" size="small">
      <table class="sys-runtime-telemetry-table" data-testid="sys-runtime-telemetry-table">
        <tbody>
          <tr>
            <th scope="row">操作系统版本</th>
            <td>
              <div class="cell-flex">
                <span class="font-mono text-xs snapshot-value">{{ sys?.osDescription || '检测中...' }}</span>
                <n-tag :type="sys?.isWin7 ? 'warning' : 'info'" size="small" :bordered="false" class="shrink-0">
                  {{ sys?.isWin7 ? 'Win7 SP1 兼容模式' : '现代桌面系统' }}
                </n-tag>
              </div>
            </td>
          </tr>
          <tr>
            <th scope="row">CPU / 进程架构</th>
            <td><span class="font-mono text-xs snapshot-value">OS: {{ sys?.architecture || 'X64' }} / Process: {{ sys?.processArchitecture || 'X64' }}</span></td>
          </tr>
          <tr>
            <th scope="row">.NET 运行时版本</th>
            <td><n-tag type="success" size="small" :bordered="false" class="font-mono">{{ sys?.dotnetVersion || '.NET 6.0' }}</n-tag></td>
          </tr>
          <tr>
            <th scope="row">CefSharp / Chromium 内核</th>
            <td><n-tag type="info" size="small" :bordered="false" class="font-mono">CefSharp {{ sys?.cefSharpVersion || '109.1.110' }} (Chromium 109)</n-tag></td>
          </tr>
          <tr>
            <th scope="row">前端加载安全协议</th>
            <td><span class="font-mono text-xs text-emerald-400 snapshot-value">{{ systemStore.currentProtocol }}</span></td>
          </tr>
          <tr>
            <th scope="row">TLS 安全传输层协议</th>
            <td><span class="font-mono text-xs snapshot-value">{{ sys?.tlsProtocols || 'Tls12, Tls13' }}</span></td>
          </tr>
          <tr>
            <th scope="row">GPU 硬件加速防崩状态</th>
            <td><n-tag :type="sys?.gpuAccelerationDisabled ? 'warning' : 'success'" size="small" :bordered="false">{{ sys?.gpuAccelerationDisabled ? '已强制关闭 GPU 加速 (Win7 / disable-gpu.flag)' : 'GPU 硬件加速已启用' }}</n-tag></td>
          </tr>
          <tr>
            <th scope="row">IPC 异步桥接状态</th>
            <td><n-tag :type="systemStore.isNativeHost ? 'success' : 'warning'" size="small" :bordered="false">{{ systemStore.isNativeHost ? 'window.hostBridge 已绑定 (Native)' : 'Web 开发态 Mock 桥接' }}</n-tag></td>
          </tr>
        </tbody>
      </table>
    </n-card>

    <n-card title="架构合规性自检清单" size="small">
      <div class="grid grid-cols-1 xl:grid-cols-2 gap-2.5 text-xs min-w-0">
        <div
          v-for="item in complianceItems"
          :key="item.title"
          class="setting-inset flex items-center justify-between gap-2 p-2.5 min-w-0"
        >
          <div class="flex items-center gap-2 min-w-0">
            <ShieldCheck class="w-4 h-4 text-emerald-400 shrink-0" />
            <span>{{ item.title }}</span>
          </div>
          <span class="font-mono text-[11px] text-secondary text-right break-words min-w-0">{{ item.value }}</span>
        </div>
      </div>
    </n-card>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { ShieldCheck } from 'lucide-vue-next';
import { useSystemStore } from '@/stores/systemStore';

const systemStore = useSystemStore();
const sys = computed(() => systemStore.systemInfo);

const complianceItems = [
  { title: '浏览器内核永久锁定', value: 'CefSharp.WinForms 109.1.110' },
  { title: '.NET 目标框架锁定', value: 'net6.0-windows (SDK 6.0.428)' },
  { title: 'Vite 编译降级目标', value: 'build.target = chrome109' },
  { title: '自定义本地安全协议', value: 'app://local (替代 file://)' },
  { title: '图标与字体回退方案', value: '纯内联 Lucide SVG + 雅黑字体栈' },
  { title: '两级引导持久化存储', value: 'bootstrap.json -> C:\\HaloAgentData' },
];
</script>
<style scoped>
/* 原生 table：与代理快照表同策略，固定布局撑满卡片，label 定宽，内容约束在表内 */
.sys-runtime-telemetry-table { table-layout: fixed; width: 100%; max-width: 100%; box-sizing: border-box; border-collapse: collapse; border: 1px solid var(--border-subtle); border-radius: 6px; overflow: hidden; }
.sys-runtime-telemetry-table th {
  width: 184px; min-width: 184px; max-width: 184px;
  text-align: left; font-weight: 500; font-size: 12px;
  padding: 6px 10px; border: 1px solid var(--border-subtle);
  white-space: normal; overflow-wrap: break-word; word-break: normal;
  line-height: 1.5; vertical-align: top;
}
.sys-runtime-telemetry-table td {
  padding: 6px 10px; border: 1px solid var(--border-subtle);
  white-space: normal; overflow-wrap: break-word; word-break: normal;
  line-height: 1.6; vertical-align: top; min-width: 0; max-width: 100%;
}
.sys-runtime-telemetry-table .cell-flex { display: flex; align-items: center; gap: 8px; min-width: 0; flex-wrap: wrap; }
.sys-runtime-telemetry-table .snapshot-value {
  min-width: 0; white-space: normal; overflow-wrap: break-word; word-break: normal;
}
</style>
