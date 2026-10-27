<!-- src/Web.Client/src/components/layout/DetailPane.vue -->
<script setup lang="ts">
import { ref } from 'vue';
import HTabs, { type TabItem } from '@/components/base/HTabs.vue';
import HEmpty from '@/components/base/HEmpty.vue';
import HScroll from '@/components/base/HScroll.vue';
import HDivider from '@/components/base/HDivider.vue';
import { useSessionStore } from '@/stores/session';

/**
 * 右栏：会话详情 / 工具调用 / 文件变更。
 *
 * ============ P4 只做骨架 ============
 *  三个 tab 的实际内容在 P5 实现。这里保证：
 *    ① tab 切换正确
 *    ② 无内容时显示空态
 *    ③ 会话元信息（cwd / mode / usage）正确显示
 * ============ ============
 */
const session = useSessionStore();

const tabs: TabItem[] = [
  { key: 'info', label: '信息' },
  { key: 'tools', label: '工具' },
  { key: 'files', label: '文件' },
];

const active = ref('info');
</script>

<template>
  <div class="flex h-full min-h-0 flex-col">
    <div class="shrink-0 px-3 pt-2">
      <HTabs v-model="active" :items="tabs" variant="pill" />
    </div>

    <HScroll class="min-h-0 flex-1">
      <div class="p-3">
        <!-- 信息 -->
        <div v-if="active === 'info'" class="space-y-3">
          <div>
            <div class="mb-1 text-2xs font-medium text-fg-subtle">工作目录</div>
            <div class="break-all font-mono text-xs text-fg">{{ session.cwd || '—' }}</div>
          </div>

          <HDivider />

          <div class="grid grid-cols-2 gap-2 text-xs">
            <div>
              <div class="text-2xs text-fg-subtle">权限模式</div>
              <div class="text-fg">{{ session.mode }}</div>
            </div>
            <div>
              <div class="text-2xs text-fg-subtle">人格</div>
              <div class="text-fg">{{ session.persona || '—' }}</div>
            </div>
            <div>
              <div class="text-2xs text-fg-subtle">轮次上限</div>
              <div class="text-fg tabular-nums">{{ session.maxTurns || '不限' }}</div>
            </div>
            <div>
              <div class="text-2xs text-fg-subtle">重试次数</div>
              <div class="text-fg tabular-nums">{{ session.retry }}</div>
            </div>
          </div>

          <HDivider label="用量" />

          <div class="grid grid-cols-2 gap-2 text-xs">
            <div>
              <div class="text-2xs text-fg-subtle">输入 token</div>
              <div class="font-mono text-fg tabular-nums">{{ session.usage.input }}</div>
            </div>
            <div>
              <div class="text-2xs text-fg-subtle">输出 token</div>
              <div class="font-mono text-fg tabular-nums">{{ session.usage.output }}</div>
            </div>
          </div>
        </div>

        <!-- 工具 -->
        <HEmpty
          v-else-if="active === 'tools'"
          icon="terminal"
          title="暂无工具调用"
          description="AI 调用工具后，这里会显示每次调用的参数与结果。"
          size="sm"
        />

        <!-- 文件 -->
        <HEmpty
          v-else-if="active === 'files'"
          icon="file-text"
          title="暂无文件变更"
          description="AI 修改文件后，这里会列出变更并可以撤销。"
          size="sm"
        />
      </div>
    </HScroll>
  </div>
</template>
