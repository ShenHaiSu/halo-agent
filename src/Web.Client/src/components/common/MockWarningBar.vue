 <script setup lang="ts">
 import { transport } from '@/transport';
 import HaloIcon from '@/components/icons/HaloIcon.vue';
 </script>

<template>
   <div v-if="transport.isMock" class="mock-warning" role="alert">
     <HaloIcon name="alert" :size="12" aria-hidden="true" />
     <span>Mock 模式：所有数据仅存在于内存，<strong>不会保存到磁盘</strong>，且不反映真实后端行为。请勿在此模式下验收联调。</span>
   </div>
</template>

<style scoped>
 .mock-warning {
   position: fixed;
   top: 0;
   left: 0;
   right: 0;
   z-index: var(--z-toast);
   display: flex;
   align-items: center;
   justify-content: center;
   gap: var(--space-1-5);
   padding: var(--space-1-5) var(--space-3);
   background: hsl(var(--danger-subtle));
   color: hsl(var(--danger));
   border-bottom: 1px solid hsl(var(--danger) / 0.4);
   font-size: var(--text-xs);
   text-align: center;
 }
</style>
