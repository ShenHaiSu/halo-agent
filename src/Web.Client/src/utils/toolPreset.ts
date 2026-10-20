/** need17-01 前端镜像：三态切换只用 preset，不再点名单个 tool。 */
export const TOOL_PRESET_CODING = 'coding';
export const TOOL_PRESET_READONLY = 'readonly';
export const TOOL_PRESET_FULL = 'full';

export function normalizePreset(p?: string | null): string {
  const v = String(p || '')
    .trim()
    .toLowerCase();
  if (v === TOOL_PRESET_READONLY || v === TOOL_PRESET_FULL) return v;
  return TOOL_PRESET_CODING;
}

export function presetLabel(p?: string | null): string {
  const n = normalizePreset(p);
  if (n === TOOL_PRESET_FULL) return 'Full';
  if (n === TOOL_PRESET_READONLY) return 'ReadOnly';
  return 'Coding';
}

export function dotColor(preset?: string | null): string {
  const n = normalizePreset(preset);
  if (n === TOOL_PRESET_FULL) return '#8b5cf6';
  if (n === TOOL_PRESET_READONLY) return '#94a3b8';
  return '#22c55e';
}
