<template>
  <div class="agent-behavior-fields">
    <div class="agent-setting-row" :class="{ 'is-changed': isDirty('subAgentEnabled') }">
      <label class="agent-field-label">总开关</label>
      <n-switch
        :value="cfg.subAgentEnabled"
        data-testid="agent-sub-enabled"
        aria-label="启用子代理"
        @update:value="(v: boolean) => emit('bool', 'subAgentEnabled', v)"
      />
      <n-button
        size="tiny"
        quaternary
        circle
        aria-label="恢复总开关默认"
        title="恢复总开关默认"
        data-testid="agent-sub-reset-enabled"
        @click="emit('reset', 'subEnabled')"
      >
        <template #icon><RotateCcw :size="13" /></template>
      </n-button>
    </div>

    <div class="agent-setting-section" :class="{ 'is-section-disabled': !cfg.subAgentEnabled }">
      <div class="agent-section-title">容量</div>
      <div class="agent-setting-row" :class="{ 'is-changed': isDirty('subMaxParallel') }">
        <label class="agent-field-label">并发上限</label>
        <n-input-number
          :value="cfg.subMaxParallel"
          :min="SUB_MAX_PARALLEL_MIN"
          :max="SUB_MAX_PARALLEL_MAX"
          :disabled="!cfg.subAgentEnabled"
          size="small"
          class="w-44 font-mono"
          data-testid="agent-sub-parallel-input"
          data-agent-field="subMaxParallel"
          aria-label="子代理并发上限"
          @update:value="(v: number | null) => emit('num', 'subMaxParallel', v)"
        />
        <n-button
          size="tiny"
          quaternary
          circle
          aria-label="恢复并发上限默认"
          title="恢复并发上限默认"
          data-testid="agent-sub-reset-parallel"
          @click="emit('reset', 'subParallel')"
        >
          <template #icon><RotateCcw :size="13" /></template>
        </n-button>
      </div>
      <div class="agent-setting-row" :class="{ 'is-changed': isDirty('subMaxIterations') }">
        <label class="agent-field-label">子轮次上限</label>
        <n-input-number
          :value="cfg.subMaxIterations"
          :min="SUB_MAX_ITERATIONS_MIN"
          :max="SUB_MAX_ITERATIONS_MAX"
          :disabled="!cfg.subAgentEnabled"
          size="small"
          class="w-44 font-mono"
          data-testid="agent-sub-iterations-input"
          data-agent-field="subMaxIterations"
          aria-label="每个子代理最大轮次"
          @update:value="(v: number | null) => emit('num', 'subMaxIterations', v)"
        />
        <n-button
          size="tiny"
          quaternary
          circle
          aria-label="恢复子轮次上限默认"
          title="恢复子轮次上限默认"
          data-testid="agent-sub-reset-iterations"
          @click="emit('reset', 'subIterations')"
        >
          <template #icon><RotateCcw :size="13" /></template>
        </n-button>
      </div>

      <div class="agent-section-title">权限</div>
      <div
        class="agent-setting-row agent-permission-grid"
        :class="{
          'is-changed':
            isDirty('subAllowExecCommand') ||
            isDirty('subAllowFileWrite') ||
            isDirty('subAllowSkillWrite') ||
            isDirty('subAllowMemoryWrite'),
        }"
      >
        <div class="agent-permission-item">
          <label class="agent-field-label">exec 权限</label>
          <n-switch
            :value="cfg.subAllowExecCommand"
            :disabled="!cfg.subAgentEnabled"
            data-testid="agent-sub-perm-exec"
            aria-label="允许子代理执行命令"
            @update:value="(v: boolean) => emit('bool', 'subAllowExecCommand', v)"
          />
        </div>
        <div class="agent-permission-item">
          <label class="agent-field-label">文件写</label>
          <n-switch
            :value="cfg.subAllowFileWrite"
            :disabled="!cfg.subAgentEnabled"
            data-testid="agent-sub-perm-file"
            aria-label="允许子代理写文件"
            @update:value="(v: boolean) => emit('bool', 'subAllowFileWrite', v)"
          />
        </div>
        <div class="agent-permission-item">
          <label class="agent-field-label">skill 写</label>
          <n-switch
            :value="cfg.subAllowSkillWrite"
            :disabled="!cfg.subAgentEnabled"
            data-testid="agent-sub-perm-skill"
            :class="{ 'sub-perm-danger': cfg.subAllowSkillWrite }"
            aria-label="允许子代理写技能"
            @update:value="(v: boolean) => emit('bool', 'subAllowSkillWrite', v)"
          />
        </div>
        <div class="agent-permission-item">
          <label class="agent-field-label">memory 写</label>
          <n-switch
            :value="cfg.subAllowMemoryWrite"
            :disabled="!cfg.subAgentEnabled"
            data-testid="agent-sub-perm-memory"
            :class="{ 'sub-perm-danger': cfg.subAllowMemoryWrite }"
            aria-label="允许子代理写记忆"
            @update:value="(v: boolean) => emit('bool', 'subAllowMemoryWrite', v)"
          />
        </div>
      </div>
      <div class="agent-permission-locked">
        <div class="agent-permission-item">
          <label class="agent-field-label">ask_user</label>
          <n-switch :value="false" :disabled="true" data-testid="agent-sub-perm-ask" aria-label="子代理向用户提问（v1 锁定关闭）" />
        </div>
        <AgentFieldHint text="v1 锁定：子代理不可向用户提问，后端强制为 false。" label="查看 ask_user 说明" />
        <n-button
          size="tiny"
          quaternary
          circle
          aria-label="恢复权限默认"
          title="恢复权限默认"
          data-testid="agent-sub-reset-perms"
          @click="emit('reset', 'subPerms')"
        >
          <template #icon><RotateCcw :size="13" /></template>
        </n-button>
      </div>

      <n-alert v-if="riskyPermissions" type="warning" :show-icon="true" class="agent-risk-alert" data-testid="agent-sub-risk-alert">
        已开启 skill 写或 memory 写：子代理可修改共享技能与记忆，请确认信任范围。
      </n-alert>
    </div>
  </div>
</template>
<script setup lang="ts">
import { computed } from 'vue';
import { RotateCcw } from 'lucide-vue-next';
import AgentFieldHint from '../AgentFieldHint.vue';
import {
  SUB_MAX_ITERATIONS_MAX,
  SUB_MAX_ITERATIONS_MIN,
  SUB_MAX_PARALLEL_MAX,
  SUB_MAX_PARALLEL_MIN,
  type SubAgentConfig,
  type SubBoolField,
  type SubNumField,
  type SubResetField,
} from './subAgentFields';

const props = withDefaults(defineProps<{ cfg: SubAgentConfig; dirtyFields?: string[] }>(), { dirtyFields: () => [] });
const emit = defineEmits<{
  (e: 'bool', f: SubBoolField, v: boolean): void;
  (e: 'num', f: SubNumField, v: number | null): void;
  (e: 'reset', f: SubResetField): void;
}>();

const riskyPermissions = computed(() => props.cfg.subAllowSkillWrite || props.cfg.subAllowMemoryWrite);

function isDirty(field: string): boolean {
  return props.dirtyFields.includes(field);
}
</script>
<style scoped>
.sub-perm-danger :deep(.n-switch__rail) {
  border: 1px solid #d03050;
}
</style>
