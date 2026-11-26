<template>
  <div class="w-full min-w-0" :data-turn-key="turn.turnId" :data-testid="`compact-turn-${turn.turnId}`">
    <CompactDivider
      :stub-id="turn.turnId"
      :label="label"
      :summary="summary"
      :summary-html="summaryHtml"
      :backup-file="null"
      :expanded="expanded"
      @toggle="toggle"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import CompactDivider from './CompactDivider.vue';
import { compactTurnLabel } from '../utils/compactTurn';
import { isTurnExpanded, setTurnExpanded } from '../utils/timelineExpansion';
import { renderHtmlSmart } from '../utils/markdownRender';
import type { FlatTimelineTurn } from '../utils/timelineSummary';

const props = defineProps<{ turn: FlatTimelineTurn }>();

const summary = computed(() => String(props.turn.userContent || props.turn.userPreview || ''));
const label = computed(() => compactTurnLabel(props.turn.removedMessages));
const summaryHtml = computed(() => renderHtmlSmart(summary.value, true));
const expanded = ref(isTurnExpanded(props.turn.turnId));

function toggle(): void {
  expanded.value = !expanded.value;
  setTurnExpanded(props.turn.turnId, expanded.value);
}
</script>
