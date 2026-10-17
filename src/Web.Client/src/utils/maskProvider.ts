/**
 * 用量分析 · Provider 名前端模糊化（纯展示层，不改 DB / 不改聚合口径）。
 *
 * 规则（need-usage-mask）：
 *  - 每个 Provider（按 providerId 为键）稳定映射为 5 位 `[a-z0-9]` 随机串，如 `k3f9a`。
 *  - 同一 Provider 在筛选下拉、明细表、堆叠图、饼图、tooltip 中显示同一个代号，
 *    刷新页面 / 重新查询不变（种子由 providerId 派生，非真正随机但外观等价）。
 *  - 关闭开关时一律显示真名（hover 也显示真名，由调用方传 title）。
 */

const ALPHABET = 'abcdefghijklmnopqrstuvwxyz0123456789';
const CODE_LENGTH = 5;

/** FNV-1a 32bit：稳定、无依赖、跨平台一致（不依赖 crypto，Win7/CEF 安全） */
function fnv1a32(seed: string): number {
  let h = 0x811c9dc5;
  for (let i = 0; i < seed.length; i++) {
    h ^= seed.charCodeAt(i);
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  return h >>> 0;
}

/** xorshift32：由种子递推，保证同种子输出同序列 */
function createRng(seed: number): () => number {
  let x = seed || 0x9e3779b9;
  return () => {
    x ^= x << 13; x >>>= 0;
    x ^= x >>> 17;
    x ^= x << 5; x >>>= 0;
    return x;
  };
}

const codeCache = new Map<string, string>();

/**
 * 取 Provider 的 5 位代号。为保证筛选下拉 / 明细表 / 趋势图 / 饼图显示同一个代号，
 * 统一以“真名”为 canonical key（明细行没有 providerId，只有 providerName）；
 * 无真名时才退化为 providerId，保证任何场景同一 Provider 稳定一致。
 */
export function providerCode(providerId: string, fallbackName = ''): string {
  const nameKey = (fallbackName || '').trim();
  const key = nameKey ? 'name:' + nameKey : (providerId || '').trim();
  if (!key) return '*****';
  const hit = codeCache.get(key);
  if (hit) return hit;
  const next = createRng(fnv1a32(key));
  let code = '';
  for (let i = 0; i < CODE_LENGTH; i++) {
    code += ALPHABET[next() % ALPHABET.length];
  }
  codeCache.set(key, code);
  return code;
}

/** 别名：按真名取代号（无 providerId 场景的便捷入口） */
export function providerCodeByName(name: string): string {
  return providerCode('', name);
}

/**
 * 统一出口：masked=true 返回代号，否则返回原名。
 * providerId 缺失时自动退化为按名字取码，保证同一名字显示一致。
 */
export function displayProviderName(
  providerId: string | null | undefined,
  realName: string,
  masked: boolean
): string {
  if (!masked) return realName || '';
  return providerCode(providerId || '', realName);
}

/** 趋势系列 / 模型下拉：`代号/modelId` 或 `真名/modelId` */
export function displayProviderModel(
  providerId: string | null | undefined,
  realName: string,
  modelId: string,
  masked: boolean
): string {
  return displayProviderName(providerId, realName, masked) + '/' + modelId;
}

/** 饼图只有 name（无 id）时的兜底：按名字取码 */
export function displayProviderNameByName(realName: string, masked: boolean): string {
  if (!masked) return realName || '';
  return providerCodeByName(realName);
}
