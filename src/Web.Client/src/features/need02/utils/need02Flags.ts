// need02-P0：总开关矩阵统一读取。
// 四个开关全部走 localStorage，默认值 on；只有显式 'off'（大小写不敏感）才关闭。
// 写法照 useAgentStream.streamMdKillSwitch / useTimeline.turnRunKillSwitch：
// 隐私模式下 localStorage 抛错时回落默认值（全开），绝不因开关读取炸掉时间轴。

export interface Need02Flags {
  p1: boolean;
  p2: boolean;
  p3: boolean;
  p4: boolean;
}

export const NEED02_FLAG_KEYS = {
  p1: 'halo-need02-p1',
  p2: 'halo-need02-p2',
  p3: 'halo-need02-p3',
  p4: 'halo-need02-p4',
} as const;

export type Need02Phase = keyof Need02Flags;

export const NEED02_DEBUG_KEY = 'halo-need02-debug';

/** 最小 storage 口径：只要会 getItem 即可，便于单测注入假 storage。 */
export interface Need02StorageLike {
  getItem(key: string): string | null;
}

function readFlag(storage: Need02StorageLike | null, key: string, def = true): boolean {
  try {
    if (!storage || typeof storage.getItem !== 'function') return def;
    const raw = storage.getItem(key);
    const v = String(raw || '').toLowerCase();
    if (!v) return def;
    return v !== 'off';
  } catch {
    return def;
  }
}

function resolveStorage(explicit?: Need02StorageLike | null): Need02StorageLike | null {
  if (explicit !== undefined) return explicit;
  try {
    if (typeof window !== 'undefined' && window.localStorage) return window.localStorage as Need02StorageLike;
  } catch {
    return null;
  }
  return null;
}

/** 纯函数入口：单测走这里（注入假 storage），与 window 解耦。 */
export function readNeed02Flags(storage?: Need02StorageLike | null): Need02Flags {
  const s = resolveStorage(storage);
  return {
    p1: readFlag(s, NEED02_FLAG_KEYS.p1, true),
    p2: readFlag(s, NEED02_FLAG_KEYS.p2, true),
    p3: readFlag(s, NEED02_FLAG_KEYS.p3, true),
    p4: readFlag(s, NEED02_FLAG_KEYS.p4, true),
  };
}

/** 组合式入口：运行时走这里（读真实 localStorage，一次快照，与既有 kill switch 语义一致）。 */
export function useNeed02Flags(): Need02Flags {
  return readNeed02Flags();
}

/**
 * P2 投影分支选择（P0 只定契约，真正切链在 P2 实现）：
 * - p2 on → 走新链 UserTurnBlock（自动旁路 need-01 的 halo-turn-run 开关，无需用户手动关）；
 * - p2 off → 回落旧链（是否再做 turn-run 合并，看 halo-turn-run 旧开关）。
 */
export type Need02TimelineMode = 'user-turn' | 'legacy-turn-run' | 'legacy-turn';

export function resolveTimelineMode(flags: Need02Flags, turnRunKillSwitchOff: boolean): Need02TimelineMode {
  if (flags.p2) return 'user-turn';
  if (turnRunKillSwitchOff) return 'legacy-turn';
  return 'legacy-turn-run';
}

/** 模板透传串：`data-need02="p1:on,p2:off,..."`，P0 验收截图对比用。 */
export function need02Attr(flags: Need02Flags): string {
  const on = (v: boolean): string => (v ? 'on' : 'off');
  return `p1:${on(flags.p1)},p2:${on(flags.p2)},p3:${on(flags.p3)},p4:${on(flags.p4)}`;
}
 
 /** need02-P4：子开关（`halo-need02-p4-<sub>`，默认 on；master p4=off 时全关）。 */
 export type Need02P4Sub = 'proj' | 'md' | 'reload' | 'window';
 
 export const NEED02_P4_SUB_KEYS: Record<Need02P4Sub, string> = {
   proj: 'halo-need02-p4-proj',
   md: 'halo-need02-p4-md',
   reload: 'halo-need02-p4-reload',
   window: 'halo-need02-p4-window',
 };
 
 /** P4 子项是否生效（纯函数，可注入假 storage；master 关则子项恒关）。 */
 export function isP4SubOn(sub: Need02P4Sub, storage?: Need02StorageLike | null, flags?: Need02Flags): boolean {
   try {
     const f = flags || readNeed02Flags(storage);
     if (!f.p4) return false;
     const s = resolveStorage(storage);
     return readFlag(s, NEED02_P4_SUB_KEYS[sub], true);
   } catch {
     return true;
   }
 }
