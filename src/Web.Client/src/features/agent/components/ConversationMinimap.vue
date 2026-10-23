<template>
  <nav v-if="visible" class="conversation-minimap" aria-label="会话导航">
    <button
      v-for="(m, i) in markers"
      :key="m.key"
      type="button"
      class="minimap-dash"
      :class="[m.kind, { active: i === activeIndex, earlier: m.kind === 'earlier' }]"
      :style="{ '--magnify': String(magnify[i] ?? 1) }"
      :aria-label="m.kind === 'earlier' ? '更早消息' : `${m.kind === 'user' ? '用户' : '助手'}：${m.preview.slice(0, 40)}`"
      :aria-current="i === activeIndex ? 'true' : undefined"
      :aria-busy="m.loading ? 'true' : undefined"
      :disabled="!!m.loading"
      :title="m.preview.slice(0, 120)"
      @click="jump(m)"
      @mouseenter="hover(i, $event)"
      @focus="hover(i, null)"
      @mouseleave="unhover"
    />
    <div v-if="popover" class="minimap-popover" :style="{ top: `${popover.top}px` }" pointer-events-none>
      {{ popover.text }}
    </div>
  </nav>
</template>
<script setup lang="ts">
import { computed, ref } from 'vue';
import { MINIMAP_POPOVER_H, magnifyFor, type MinimapMarker } from '@features/session/utils/conversation-minimap';

const props = defineProps<{
  markers: MinimapMarker[];
  activeIndex: number;
  railHeight: number;
}>();
const emit = defineEmits<{ jump: [marker: MinimapMarker] }>();

const visible = computed(() => props.markers.length > 0);
const magnify = ref<number[]>(props.markers.map(() => 1));
const popover = ref<{ top: number; text: string } | null>(null);

function jump(m: MinimapMarker): void {
  emit('jump', m);
}

function hover(i: number, e: MouseEvent | null): void {
  magnify.value = props.markers.map((_, j) => magnifyFor(Math.abs(j - i) * 8));
  const m = props.markers[i];
  if (!m) return;
  const center = m.offsetTop;
  const top = Math.max(0, Math.min(props.railHeight - MINIMAP_POPOVER_H, center - 36));
  void e;
  popover.value = { top, text: m.preview.split('\n').slice(0, 5).join('\n') };
}

function unhover(): void {
  magnify.value = props.markers.map(() => 1);
  popover.value = null;
}
</script>
<style scoped>
.conversation-minimap {
  position: absolute;
  left: 0;
  top: 0;
  bottom: 0;
  width: 26px;
  padding: 8px 0 8px 8px;
  display: flex;
  flex-direction: column;
  gap: 3px;
  pointer-events: auto;
}
.minimap-dash {
  width: 8px;
  height: 18px;
  border-radius: 4px;
  transform: scaleX(var(--magnify, 1));
  transition: transform 90ms ease;
  background: var(--n-text-color-disabled, #8883);
  border: none;
  cursor: pointer;
  padding: 0;
}
.minimap-dash.user {
  width: 13px;
}
.minimap-dash.assistant {
  width: 8px;
}
.minimap-dash.earlier {
  background: transparent;
  border: 1px dashed currentColor;
  height: 13px;
}
.minimap-dash:hover {
  filter: brightness(1.2);
}
.minimap-dash.active {
  background: var(--n-primary-color, #2080f0);
}
.minimap-popover {
  position: absolute;
  left: 24px;
  width: 248px;
  height: 132px;
  overflow: hidden;
  font-size: 11px;
  line-height: 1.4;
  white-space: pre-wrap;
  background: var(--n-popover-color, #fff);
  border: 1px solid var(--n-border-color, #eee);
  border-radius: 6px;
  padding: 8px;
  pointer-events: none;
  z-index: 20;
}
</style>
