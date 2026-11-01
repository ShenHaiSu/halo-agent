<!-- src/Web.Client/src/components/common/BootSplash.vue -->
<!-- 纯前端启动动画：dist 下前端常比引擎先就绪，用全屏遮罩盖住首屏并延迟初始请求，
     绕开“引擎桥接不可用”类抢跑误报。本组件不读任何引擎数据，只读 boot 门状态。 -->
<script setup lang="ts">
import { computed } from 'vue';
import { useBootStore } from '@/stores/boot';
import HSpinner from '@/components/base/HSpinner.vue';

const boot = useBootStore();
const show = computed(() => boot.phase === 'boot');
</script>

<template>
  <Transition name="boot-fade">
    <div
      v-if="show"
      class="boot-splash"
      role="status"
      aria-busy="true"
      aria-label="正在启动"
      data-testid="boot-splash"
    >
      <div class="boot-card">
        <HSpinner size="lg" />
        <div class="boot-title">HaloAgent 正在启动</div>
        <div class="boot-sub">{{ boot.note }}</div>
        <div class="boot-bar" aria-hidden="true"><span class="boot-fill" /></div>
      </div>
    </div>
  </Transition>
</template>

<style scoped>
.boot-splash {
  position: fixed;
  inset: 0;
  z-index: var(--z-modal);
  display: flex;
  align-items: center;
  justify-content: center;
  background: hsl(var(--bg));
}

.boot-card {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--space-3);
  padding: var(--space-8) var(--space-10);
  border: 1px solid hsl(var(--border-subtle));
  border-radius: var(--radius-lg);
  background: hsl(var(--bg-elevated));
  box-shadow: var(--shadow-md);
  min-width: 15rem;
}

.boot-title {
  font-size: var(--text-base);
  font-weight: var(--weight-semibold);
  color: hsl(var(--fg));
}

.boot-sub {
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
  min-height: 1.25rem;
}

.boot-bar {
  width: 12rem;
  height: 0.25rem;
  overflow: hidden;
  border-radius: var(--radius-full);
  background: hsl(var(--bg-muted));
}
.boot-fill {
  display: block;
  width: 40%;
  height: 100%;
  border-radius: var(--radius-full);
  background: hsl(var(--accent));
  animation: boot-slide 1.1s ease-in-out infinite;
}

@keyframes boot-slide {
  0% {
    transform: translateX(-100%);
  }
  100% {
    transform: translateX(30rem);
  }
}

.boot-fade-leave-active {
  transition: opacity 0.3s ease;
}

.boot-fade-leave-to {
  opacity: 0;
}

@media (prefers-reduced-motion: reduce) {
  .boot-fill {
    animation: none;
    width: 100%;
  }
  .boot-fade-leave-active {
    transition: none;
  }
}
</style>
