// src/Web.Client/src/components/usage/providerMask.ts
// Provider 纯前端模糊化：默认开，恒 5 位 [a-z0-9] 确定性假名。
// 只做显示映射，绝不回写请求参数（providerId 过滤恒用真值）。

/** localStorage 开关：'1'=开，'0'=关，缺省=开。 */
export const MASK_KEY = 'halo.usage.maskProvider';

const ALPHABET = '0123456789abcdefghijklmnopqrstuvwxyz';

/** FNV-1a 32bit（pid 按 utf8 编码，空串/中文/emoji 不抛）。 */
function fnv1a32(bytes: Uint8Array): number {
  let h = 2166136261;
  for (const b of bytes) {
    h ^= b;
    h = Math.imul(h, 16777619);
  }
  return h >>> 0;
}

/** mulberry32 PRNG（确定性，由 FNV 哈希做种子）。 */
function mulberry32(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a |= 0;
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** 恒 5 位 [a-z0-9] 确定性假名；36^5≈60M，碰撞直接接受，保证刷新稳定。 */
export function maskProviderId(pid: string): string {
  const bytes = new TextEncoder().encode(pid);
  const rand = mulberry32(fnv1a32(bytes));
  let out = '';
  for (let i = 0; i < 5; i++) {
    const byte = Math.floor(rand() * 256);
    out += ALPHABET[byte % 36];
  }
  return out;
}

export function isMaskEnabled(): boolean {
  try {
    return localStorage.getItem(MASK_KEY) !== '0';
  } catch {
    return true;
  }
}

export function setMaskEnabled(v: boolean): void {
  try {
    localStorage.setItem(MASK_KEY, v ? '1' : '0');
  } catch {
    /* 忽略：无痕/禁用存储时保持内存默认（开） */
  }
}

/** 显示唯一入口：开时全页假名，关时还原真名（display 为空回落 pid）。 */
export function resolveProviderDisplay(pid: string, display: string): string {
  if (!isMaskEnabled()) return display || pid;
  return maskProviderId(pid);
}
