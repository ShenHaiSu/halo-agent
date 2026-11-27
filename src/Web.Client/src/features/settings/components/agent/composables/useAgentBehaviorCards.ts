import { computed, reactive } from 'vue';
import { AGENT_BEHAVIOR_CARD_NAMES, CONFIG_FIELD_CARD, type AgentBehaviorCardName } from '../agentBehaviorTypes';

export const DEFAULT_OPEN: Readonly<Record<AgentBehaviorCardName, boolean>> = {
  timeout: true,
  iterations: true,
  subagents: true,
  monitor: false,
  rejection: false,
};

export function useAgentBehaviorCards() {
  const expanded = reactive<Record<AgentBehaviorCardName, boolean>>({ ...DEFAULT_OPEN });
  const allOpen = computed(() => AGENT_BEHAVIOR_CARD_NAMES.every((name) => expanded[name]));

  function setOpen(name: AgentBehaviorCardName, open: boolean): void {
    expanded[name] = open;
  }

  function toggle(name: AgentBehaviorCardName): void {
    expanded[name] = !expanded[name];
  }

  function toggleAll(): void {
    const shouldOpen = !AGENT_BEHAVIOR_CARD_NAMES.some((name) => expanded[name]);
    for (const name of AGENT_BEHAVIOR_CARD_NAMES) {
      expanded[name] = shouldOpen;
    }
  }

  function isCardDirty(name: AgentBehaviorCardName, dirtyFields: string[]): boolean {
    if (name === 'monitor' || name === 'rejection') return false;
    return dirtyFields.some((field) => CONFIG_FIELD_CARD[field] === name);
  }

  return { expanded, allOpen, setOpen, toggle, toggleAll, isCardDirty };
}
