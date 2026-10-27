<!-- src/Web.Client/src/components/__gallery/sections/FeedbackSection.vue -->
<!-- D4-9 画廊：反馈分区（toast / dialog / drawer / tooltip / skeleton）。 -->
<script setup lang="ts">
import { ref } from 'vue';
import { useToast } from '@/composables/useToast';

const toast = useToast();

const dialogOpen = ref(false);
const persistentDialog = ref(false);
const drawerOpen = ref(false);
const drawerSide = ref<'left' | 'right' | 'top' | 'bottom'>('right');
const popoverOpen = ref(false);

const btnGhost =
  'rounded-md px-3 py-1.5 text-sm text-fg-muted transition-colors hover:bg-bg-muted hover:text-fg';
const btnSecondary =
  'rounded-md border border-border bg-bg-elevated px-3 py-1.5 text-sm text-fg transition-colors hover:border-border-strong';
const btnPrimary =
  'rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg transition-colors hover:bg-accent-hover';

function showToast(level: 'info' | 'success' | 'warning' | 'error') {
  const map = {
    info: '这是一条普通提示',
    success: '保存成功',
    warning: '磁盘空间不足 10%',
    error: '连接失败：请检查网络代理设置',
  };
  toast[level](map[level]);
}

function showActionToast() {
  toast.error('文件已删除', {
    action: { label: '撤销', onClick: () => toast.success('已撤销') },
    duration: 8000,
  });
}
</script>

<template>
  <div class="space-y-8">
    <section>
      <h2 class="mb-3 text-lg font-semibold">Toast（右下角）</h2>
      <div class="flex flex-wrap gap-2">
        <button type="button" :class="btnGhost" @click="showToast('info')">信息</button>
        <button type="button" :class="btnGhost" @click="showToast('success')">成功</button>
        <button type="button" :class="btnGhost" @click="showToast('warning')">警告</button>
        <button type="button" :class="btnGhost" @click="showToast('error')">错误</button>
        <button type="button" :class="btnSecondary" @click="showActionToast">带操作按钮</button>
      </div>
      <p class="mt-2 text-xs text-fg-subtle">最多同时显示 3 条。错误 6 秒、其他 3 秒自动消失。</p>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">Spinner</h2>
      <div class="flex items-center gap-6">
        <span
          class="h-3 w-3 animate-spin rounded-full border-2 border-border-strong border-t-accent"
        />
        <span
          class="h-4 w-4 animate-spin rounded-full border-2 border-border-strong border-t-accent"
        />
        <span
          class="h-6 w-6 animate-spin rounded-full border-2 border-border-strong border-t-accent"
        />
        <span
          class="h-8 w-8 animate-spin rounded-full border-2 border-border-strong border-t-accent"
        />
      </div>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">Tooltip（原生 title 占位）</h2>
      <div class="flex flex-wrap gap-3">
        <button type="button" :class="btnSecondary" title="上方提示">上</button>
        <button type="button" :class="btnSecondary" title="下方提示">下</button>
        <button type="button" :class="btnSecondary" title="左侧提示">左</button>
        <button type="button" :class="btnSecondary" title="右侧提示">右</button>
      </div>
      <p class="mt-2 text-xs text-fg-subtle">
        D4-5 的 HTooltip 会替换这里（自动翻转 + 长文本换行）。
      </p>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">Popover</h2>
      <div class="relative inline-block">
        <button type="button" :class="btnSecondary" @click="popoverOpen = !popoverOpen">
          筛选条件
        </button>
        <div
          v-if="popoverOpen"
          class="absolute left-0 top-full z-20 mt-1 w-64 rounded-md border border-border bg-bg-elevated p-3 shadow-md"
        >
          <p class="mb-2 text-xs font-medium text-fg">筛选</p>
          <p class="text-xs text-fg-muted">点外部或按 ESC 关闭（此处简化为按钮切换）。</p>
          <div class="mt-2 flex gap-2">
            <button
              type="button"
              :class="btnPrimary"
              class="px-2 py-1 text-xs"
              @click="popoverOpen = false"
            >
              应用
            </button>
            <button
              type="button"
              :class="btnGhost"
              class="px-2 py-1 text-xs"
              @click="popoverOpen = false"
            >
              取消
            </button>
          </div>
        </div>
      </div>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">Dialog</h2>
      <div class="flex flex-wrap gap-2">
        <button type="button" :class="btnPrimary" @click="dialogOpen = true">打开对话框</button>
        <button type="button" :class="btnSecondary" @click="persistentDialog = true">
          不可ESC关闭
        </button>
      </div>
      <p class="mt-2 text-xs text-fg-subtle">
        验证：ESC 关闭、关闭后焦点回到触发按钮（D4-5 的 HDialog 含焦点陷阱）。
      </p>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">Drawer</h2>
      <div class="flex flex-wrap gap-2">
        <button
          v-for="s in ['left', 'right', 'top', 'bottom'] as const"
          :key="s"
          type="button"
          :class="btnSecondary"
          @click="
            drawerSide = s;
            drawerOpen = true;
          "
        >
          {{ s }}
        </button>
      </div>
    </section>

    <hr class="border-border-subtle" />

    <section>
      <h2 class="mb-3 text-lg font-semibold">骨架屏</h2>
      <div class="max-w-md space-y-2">
        <div class="h-3 animate-pulse rounded bg-bg-muted" />
        <div class="h-3 w-5/6 animate-pulse rounded bg-bg-muted" />
        <div class="h-3 w-2/3 animate-pulse rounded bg-bg-muted" />
        <div class="flex items-center gap-3 pt-2">
          <div class="h-10 w-10 animate-pulse rounded-full bg-bg-muted" />
          <div class="h-20 flex-1 animate-pulse rounded-md bg-bg-muted" />
        </div>
      </div>
    </section>

    <!-- Dialog 实例 -->
    <div
      v-if="dialogOpen"
      class="fixed inset-0 z-30 flex items-center justify-center bg-bg-overlay"
      role="dialog"
      aria-modal="true"
      aria-label="重命名会话"
      @keydown.esc="dialogOpen = false"
      @click.self="dialogOpen = false"
    >
      <div class="w-full max-w-sm rounded-lg border border-border bg-bg p-4 shadow-lg">
        <h3 class="mb-1 text-base font-semibold">重命名会话</h3>
        <p class="mb-3 text-sm text-fg-muted">给这个会话起一个容易认出的名字。</p>
        <input
          class="w-full rounded-md border border-border bg-bg px-2.5 py-1.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40"
          placeholder="例如：整理下载目录"
        />
        <div class="mt-4 flex justify-end gap-2">
          <button type="button" :class="btnGhost" @click="dialogOpen = false">取消</button>
          <button type="button" :class="btnPrimary" @click="dialogOpen = false">保存</button>
        </div>
      </div>
    </div>

    <div
      v-if="persistentDialog"
      class="fixed inset-0 z-30 flex items-center justify-center bg-bg-overlay"
      role="dialog"
      aria-modal="true"
      aria-label="不可关闭的对话框"
      @click.self="() => {}"
    >
      <div class="w-full max-w-sm rounded-lg border border-border bg-bg p-4 shadow-lg">
        <h3 class="mb-1 text-base font-semibold">不可 ESC 关闭</h3>
        <p class="text-sm text-fg-muted">只能点按钮关闭（persistent）。</p>
        <div class="mt-4 flex justify-end gap-2">
          <button type="button" :class="btnPrimary" @click="persistentDialog = false">
            我知道了
          </button>
        </div>
      </div>
    </div>

    <!-- Drawer 实例 -->
    <div
      v-if="drawerOpen"
      class="fixed inset-0 z-30 bg-bg-overlay"
      @click.self="drawerOpen = false"
      @keydown.esc="drawerOpen = false"
    >
      <div
        class="absolute bg-bg p-4 shadow-lg"
        :class="{
          'left-0 top-0 h-full w-72 border-r border-border': drawerSide === 'left',
          'right-0 top-0 h-full w-72 border-l border-border': drawerSide === 'right',
          'left-0 top-0 h-56 w-full border-b border-border': drawerSide === 'top',
          'bottom-0 left-0 h-56 w-full border-t border-border': drawerSide === 'bottom',
        }"
        role="dialog"
        aria-modal="true"
        aria-label="抽屉"
      >
        <h3 class="mb-2 text-base font-semibold">抽屉标题（{{ drawerSide }}）</h3>
        <p class="text-xs text-fg-muted">验证：ESC 关闭、关闭后焦点归还。</p>
        <div class="mt-4 flex gap-2">
          <button type="button" :class="btnGhost" @click="drawerOpen = false">取消</button>
          <button type="button" :class="btnPrimary" @click="drawerOpen = false">确定</button>
        </div>
      </div>
    </div>
  </div>
</template>
