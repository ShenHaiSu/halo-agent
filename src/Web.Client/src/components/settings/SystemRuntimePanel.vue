<template>
  <div class="space-y-4 min-w-0 max-w-full overflow-x-hidden">
    <n-card title="跨代宿主内核与系统环境遥测" size="small">
      <n-descriptions bordered :column="1" size="small" label-placement="left">
        <n-descriptions-item label="操作系统版本">
          <div class="flex items-center gap-2">
            <span class="font-mono text-xs min-w-0 break-all">{{ sys?.osDescription || '检测中...' }}</span>
            <n-tag :type="sys?.isWin7 ? 'warning' : 'info'" size="small" :bordered="false">
              {{ sys?.isWin7 ? 'Win7 SP1 兼容模式' : '现代桌面系统' }}
            </n-tag>
          </div>
        </n-descriptions-item>

        <n-descriptions-item label="CPU / 进程架构">
          <span class="font-mono text-xs min-w-0 break-all">
            OS: {{ sys?.architecture || 'X64' }} / Process: {{ sys?.processArchitecture || 'X64' }}
          </span>
        </n-descriptions-item>

        <n-descriptions-item label=".NET 运行时版本">
          <n-tag type="success" size="small" :bordered="false" class="font-mono">
            {{ sys?.dotnetVersion || '.NET 6.0' }}
          </n-tag>
        </n-descriptions-item>

        <n-descriptions-item label="CefSharp / Chromium 内核">
          <n-tag type="info" size="small" :bordered="false" class="font-mono">
            CefSharp {{ sys?.cefSharpVersion || '109.1.110' }} (Chromium 109)
          </n-tag>
        </n-descriptions-item>

        <n-descriptions-item label="前端加载安全协议">
          <span class="font-mono text-xs text-emerald-400 min-w-0 break-all">
            {{ systemStore.currentProtocol }}
          </span>
        </n-descriptions-item>

        <n-descriptions-item label="TLS 安全传输层协议">
          <span class="font-mono text-xs min-w-0 break-all">{{ sys?.tlsProtocols || 'Tls12, Tls13' }}</span>
        </n-descriptions-item>

        <n-descriptions-item label="GPU 硬件加速防崩状态">
          <n-tag :type="sys?.gpuAccelerationDisabled ? 'warning' : 'success'" size="small" :bordered="false">
            {{
              sys?.gpuAccelerationDisabled
                ? '已强制关闭 GPU 加速 (Win7 / disable-gpu.flag)'
                : 'GPU 硬件加速已启用'
            }}
          </n-tag>
        </n-descriptions-item>

        <n-descriptions-item label="IPC 异步桥接状态">
          <n-tag :type="systemStore.isNativeHost ? 'success' : 'warning'" size="small" :bordered="false">
            {{ systemStore.isNativeHost ? 'window.hostBridge 已绑定 (Native)' : 'Web 开发态 Mock 桥接' }}
          </n-tag>
        </n-descriptions-item>
      </n-descriptions>
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
          <span class="font-mono text-[11px] text-secondary text-right break-all min-w-0">{{ item.value }}</span>
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
