<!-- src/Web.Client/src/components/chat/StreamingCursor.vue -->
<!-- P5 D5-3 流式光标：三个跳动点，只用 opacity 动画（不触发重排），支持减少动效偏好。 -->
<template>
  <span class="cursor" aria-label="正在生成">
    <span class="dot" /><span class="dot" /><span class="dot" />
  </span>
</template>

<style scoped>
.cursor {
  display: inline-flex;
  gap: 3px;
  padding: 4px 0;
}
.dot {
  width: 5px;
  height: 5px;
  border-radius: 50%;
  background: hsl(var(--fg-subtle));
  animation: blink 1.2s infinite;
  will-change: opacity;
}
.dot:nth-child(2) {
  animation-delay: 0.2s;
}
.dot:nth-child(3) {
  animation-delay: 0.4s;
}
@keyframes blink {
  0%,
  60%,
  100% {
    opacity: 0.25;
  }
  30% {
    opacity: 1;
  }
}
@media (prefers-reduced-motion: reduce) {
  .dot {
    animation: none;
    opacity: 0.6;
  }
}
</style>
