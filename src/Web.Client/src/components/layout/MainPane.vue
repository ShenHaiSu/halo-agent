<!-- src/Web.Client/src/components/layout/MainPane.vue -->
<script setup lang="ts">
import { useMessagesStore } from '@/stores/messages';
import { useStreamStore } from '@/stores/stream';
import HEmpty from '@/components/base/HEmpty.vue';
import HSkeleton from '@/components/base/HSkeleton.vue';
import HButton from '@/components/base/HButton.vue';
import HScroll from '@/components/base/HScroll.vue';

/**
 * 主区：消息列表 + 输入区。
 *
 * ============ P4 只做骨架 ============
 *  真正的消息渲染、Markdown、工具卡片、输入框（@引用/斜杠命令）
 *  在 P5 实现。这里保证：
 *    ① 滚动容器正确（只有这里能滚）
 *    ② 空状态/加载态正确
 *    ③ 消息列表的 flex 布局正确（min-h-0）
 *    ④ 输入区固定底部
 * ============ ============
 */
const messages = useMessagesStore();
const stream = useStreamStore();
</script>

<template>
  <div class="flex h-full min-h-0 flex-col">
    <!-- 消息区 -->
    <HScroll class="min-h-0 flex-1" auto-follow>
      <div class="mx-auto w-full max-w-[var(--layout-message-max-width)] px-6 py-4">
        <!-- 加载态 -->
        <div v-if="messages.loading" class="space-y-6">
          <HSkeleton :lines="3" />
          <HSkeleton :lines="4" />
          <HSkeleton :lines="2" />
        </div>

        <!-- 空态 -->
        <HEmpty
          v-else-if="messages.isEmpty"
          icon="message-plus"
          title="开始一段对话"
          description="描述你想让 AI 做的事。它会读文件、跑命令、改代码，并把过程告诉你。"
        >
          <template #action>
            <div class="flex flex-wrap justify-center gap-2">
              <span
                class="rounded-md border border-border bg-bg-elevated px-2 py-1 text-2xs text-fg-muted"
              >
                把 Downloads 里的 zip 归档
              </span>
              <span
                class="rounded-md border border-border bg-bg-elevated px-2 py-1 text-2xs text-fg-muted"
              >
                排查 build 失败原因
              </span>
              <span
                class="rounded-md border border-border bg-bg-elevated px-2 py-1 text-2xs text-fg-muted"
              >
                给这个项目写 README
              </span>
            </div>
          </template>
        </HEmpty>

        <!-- 消息列表（P4 用最简形式，P5 会换成真正的渲染组件） -->
        <template v-else>
          <div v-for="m in messages.items" :key="m.id" class="mb-4">
            <div class="mb-1 text-2xs font-medium text-fg-subtle">
              {{ m.role === 'user' ? '你' : m.role === 'assistant' ? 'Halo' : m.role }}
            </div>
            <div
              class="whitespace-pre-wrap text-base text-fg"
              :class="m.streaming ? 'after:content-[\'▌\'] after:animate-pulse' : ''"
            >
              {{ m.content }}
            </div>
            <div
              v-if="m.reasoning"
              class="mt-1 border-l-2 border-border pl-2 text-xs text-fg-muted"
            >
              {{ m.reasoning }}
            </div>
          </div>
        </template>
      </div>
    </HScroll>

    <!-- 输入区（P4 占位，P5 实现完整输入框） -->
    <div class="shrink-0 border-t border-border bg-bg px-6 py-3">
      <div class="mx-auto w-full max-w-[var(--layout-message-max-width)]">
        <div class="flex items-end gap-2 rounded-lg border border-border bg-bg-elevated px-3 py-2">
          <div class="min-w-0 flex-1">
            <div class="text-sm text-fg-subtle">
              {{ stream.busy ? '正在生成…（按 Esc 中断）' : '输入消息…（P5 实现完整输入框）' }}
            </div>
          </div>
          <HButton
            :variant="stream.busy ? 'danger' : 'primary'"
            size="sm"
            :icon="stream.busy ? 'square' : 'send'"
            :disabled="true"
          >
            {{ stream.busy ? '停止' : '发送' }}
          </HButton>
        </div>
      </div>
    </div>
  </div>
</template>
