/** 工具失败文本标记：执行层失败输出的前缀，存量数据回退链专用。 */
export const TOOL_ERROR_MARK = '[Tool Error';

/** 文本是否携带工具失败标记（有逐工具状态位时优先用状态位，此处仅作未知数据的回退判定）。 */
export function isToolErrorText(text: string | null | undefined): boolean {
  return String(text ?? '').includes(TOOL_ERROR_MARK);
}
