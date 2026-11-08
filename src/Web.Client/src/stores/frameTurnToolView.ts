import { computed } from 'vue';
import { readOverride, resolveMode, type Surface, type SurfaceMode } from './surfaceRouter';

/**
 * Turn/tool face view source. The history, usage and settings faces keep
 * resolving to legacy while this station owns only turn and tool; every
 * face resolves through the single surface table, so a mixed state always
 * renders safely. Production mounting waits for the cutover; until then the
 * development override decides per face.
 */

/** Pure turn-face resolver with a test seam for the override list. */
export function resolveTurnMode(override?: readonly Surface[]): SurfaceMode {
  try {
    return resolveMode('turn', override);
  } catch {
    return 'legacy';
  }
}

/** Pure tool-face resolver with a test seam for the override list. */
export function resolveToolMode(override?: readonly Surface[]): SurfaceMode {
  try {
    return resolveMode('tool', override);
  } catch {
    return 'legacy';
  }
}

/** Pure source pick: frame data only when the face resolves to frame. */
export function selectTurnSource<T>(turnMode: SurfaceMode, legacyValue: T, frameValue: T): T {
  return turnMode === 'frame' ? frameValue : legacyValue;
}

/** Pure source pick: frame cards only when the face resolves to frame. */
export function selectToolSource<T>(toolMode: SurfaceMode, legacyValue: T, frameValue: T): T {
  return toolMode === 'frame' ? frameValue : legacyValue;
}

export interface MixedFaceState {
  history: SurfaceMode;
  turn: SurfaceMode;
  tool: SurfaceMode;
}

/**
 * Describe a mixed render state: safe when every frame face is implemented
 * and every other face stays on legacy; rolling back is flipping the two
 * faces to legacy with no code change.
 */
export function describeMixedState(state: MixedFaceState): { mixed: boolean; safe: boolean; rollback: string } {
  const mixed = state.turn === 'frame' || state.tool === 'frame';
  const safe = state.history === 'legacy';
  return {
    mixed,
    safe,
    rollback: '拨回 turn/tool 两面到 legacy（?surface= 置空或 localStorage 清 halo-surface-modes），不改代码',
  };
}

export function useFrameTurnToolView(devOverride?: readonly Surface[]) {
  const turnMode = computed<SurfaceMode>(() => {
    if (devOverride !== undefined) return resolveTurnMode(devOverride);
    return resolveTurnMode(readOverride());
  });
  const toolMode = computed<SurfaceMode>(() => {
    if (devOverride !== undefined) return resolveToolMode(devOverride);
    return resolveToolMode(readOverride());
  });
  const turnActive = computed(() => turnMode.value === 'frame');
  const toolActive = computed(() => toolMode.value === 'frame');
  return { turnMode, toolMode, turnActive, toolActive };
}
