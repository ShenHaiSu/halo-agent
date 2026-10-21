<template>
  <n-card title="子代理配置" size="small">
    <div class="flex items-center gap-2 flex-wrap">
      <label class="text-[13px] font-medium w-24">总开关</label>
      <n-switch :value="cfg.subAgentEnabled" data-testid="agent-sub-enabled" @update:value="(v:boolean)=>emit('bool','subAgentEnabled',v)" />
      <n-button size="tiny" data-testid="agent-sub-reset-enabled" @click="emit('reset','subEnabled')">恢复默认</n-button>
    </div>
    <div class="grid grid-cols-[96px_1fr_auto] items-center gap-2 mt-2">
      <label class="text-[13px] font-medium">并发上限</label>
      <n-input-number :value="cfg.subMaxParallel" :min="1" :max="8" size="small" class="w-44 font-mono" data-testid="agent-sub-parallel-input" @update:value="(v:number|null)=>emit('num','subMaxParallel',v)" />
      <n-button size="tiny" data-testid="agent-sub-reset-parallel" @click="emit('reset','subParallel')">恢复默认</n-button>
    </div>
    <div class="grid grid-cols-[96px_1fr_auto] items-center gap-2 mt-2">
      <label class="text-[13px] font-medium">子轮次上限</label>
      <n-input-number :value="cfg.subMaxIterations" :min="1" :max="50" size="small" class="w-44 font-mono" data-testid="agent-sub-iterations-input" @update:value="(v:number|null)=>emit('num','subMaxIterations',v)" />
      <n-button size="tiny" data-testid="agent-sub-reset-iterations" @click="emit('reset','subIterations')">恢复默认</n-button>
    </div>
    <div class="flex items-center gap-2 flex-wrap mt-2">
      <label class="text-[13px] font-medium w-24">exec 权限</label>
      <n-switch :value="cfg.subAllowExecCommand" data-testid="agent-sub-perm-exec" @update:value="(v:boolean)=>emit('bool','subAllowExecCommand',v)" />
      <label class="text-[13px] font-medium w-24">文件写</label>
      <n-switch :value="cfg.subAllowFileWrite" data-testid="agent-sub-perm-file" @update:value="(v:boolean)=>emit('bool','subAllowFileWrite',v)" />
    </div>
    <div class="flex items-center gap-2 flex-wrap mt-2">
      <label class="text-[13px] font-medium w-24">skill 写</label>
      <n-switch :value="cfg.subAllowSkillWrite" data-testid="agent-sub-perm-skill" :class="{ 'sub-perm-danger': cfg.subAllowSkillWrite }" @update:value="(v:boolean)=>emit('bool','subAllowSkillWrite',v)" />
      <label class="text-[13px] font-medium w-24">memory 写</label>
      <n-switch :value="cfg.subAllowMemoryWrite" data-testid="agent-sub-perm-memory" :class="{ 'sub-perm-danger': cfg.subAllowMemoryWrite }" @update:value="(v:boolean)=>emit('bool','subAllowMemoryWrite',v)" />
    </div>
    <div class="flex items-center gap-2 flex-wrap mt-2">
      <label class="text-[13px] font-medium w-24">ask_user</label>
      <n-switch :value="false" :disabled="true" data-testid="agent-sub-perm-ask" />
    </div>
    <div class="mt-2"><n-button size="tiny" data-testid="agent-sub-reset-perms" @click="emit('reset','subPerms')">恢复权限默认</n-button></div>
  </n-card>
</template>
<script setup lang="ts">
defineProps<{ cfg: Record<string, number | boolean> }>();
const emit = defineEmits<{ (e: 'bool', f: string, v: boolean): void; (e: 'num', f: string, v: number | null): void; (e: 'reset', f: string): void }>();
</script>
<style scoped>.sub-perm-danger :deep(.n-switch__rail) { border: 1px solid #d03050; }</style>
