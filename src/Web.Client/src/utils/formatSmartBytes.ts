/** need12 R1：流式写入字节唯一口径（人类可读在前、精确值在后）。 */
export function formatSmartBytes(bytes: number): string {
  const b = Number.isFinite(bytes) ? Math.max(0, Math.floor(bytes)) : 0;
  const exact = `${b.toLocaleString('en-US')} Bytes`;
  if (b < 1024) return exact;
  const kb = b / 1024;
  if (b < 1024 * 1024) {
    return `${kb.toFixed(kb < 10 ? 2 : 1)} KB = ${exact}`;
  }
  const mb = b / (1024 * 1024);
  const kbStr = kb.toLocaleString('en-US', { maximumFractionDigits: 1 });
  return `${mb.toFixed(2)} MB = ${kbStr} KB = ${exact}`;
}

/** 瓦片/表头用短格式（不带精确值）。 */
export function formatSmartBytesShort(bytes: number): string {
  const b = Number.isFinite(bytes) ? Math.max(0, Math.floor(bytes)) : 0;
  if (b < 1024) return `${b} B`;
  const kb = b / 1024;
  if (b < 1024 * 1024) return `${kb.toFixed(kb < 10 ? 2 : 1)} KB`;
  const mb = b / (1024 * 1024);
  if (b < 1024 * 1024 * 1024) return `${mb.toFixed(2)} MB`;
  return `${(b / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

/** token 短格式（瓦片用）：1,234 / 8.24M。 */
export function formatTokenShort(n: number): string {
  const v = Number.isFinite(n) ? Math.max(0, Math.floor(n)) : 0;
  if (v < 1000) return `${v.toLocaleString('en-US')}`;
  if (v < 1_000_000) return `${(v / 1000).toFixed(1)}K`;
  return `${(v / 1_000_000).toFixed(2)}M`;
}
