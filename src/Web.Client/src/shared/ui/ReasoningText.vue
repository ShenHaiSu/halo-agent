<template>
  <div
    v-if="text"
    class="reasoning-block min-w-0 overflow-hidden rounded-md border-l-2 border-accent bg-[var(--reasoning-block-bg)] px-2.5 py-1.5"
    data-testid="reasoning-block"
  >
    <div
      class="reasoning-seg whitespace-pre-wrap break-words select-text app-text-small text-[var(--reasoning-text)] max-h-[40vh] overflow-y-auto"
    >
      {{ text }}
    </div>
  </div>
</template>
<script setup lang="ts">
/**
 * 思考块唯一渲染壳：微底 + 2px 主题色左竖线 + 40vh 内滚。
 *
 * 为什么把“底盘”和“滚动层”拆成两层：长思考内滚时，底色/左边线/圆角不能跟着文字滚走。
 * 为什么 v-if 判空：原文可能是纯空白，裁完为空串；带底色的空圆角块在暗色下非常显眼。
 * 为什么不提供标签与尺寸参数：思考块保持匿名，字号与父抽屉正文保持固定级差。
 */
import { computed } from 'vue';
import { trimBlankEdges } from '@shared/utils/trimBlank';

const props = defineProps<{ raw: string | null | undefined }>();
const text = computed(() => trimBlankEdges(props.raw));
</script>
<style scoped>
.reasoning-seg {
  /* 与时间线视图中的同类声明一致：滚动条出现时宽度不变 */
  scrollbar-gutter: stable;
  overflow-anchor: auto;
}
</style>
