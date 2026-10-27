<script setup lang="ts">
import { transport } from '@/transport';
</script>

<template>
  <div v-if="transport.isMock" class="mock-warning" role="alert">
    ⚠️ Mock 模式：所有数据仅存在于内存，<strong>不会保存到磁盘</strong>，
    且不反映真实后端行为。请勿在此模式下验收联调。
  </div>
</template>

<style scoped>
.mock-warning {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  z-index: 99999;
  padding: 6px 12px;
  background: #b91c1c;
  color: #fff;
  font-size: 12px;
  text-align: center;
}
</style>
