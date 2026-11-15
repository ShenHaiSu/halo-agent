// need02-P4：子开关矩阵单测（p4-proj/md/reload/window 逐个独立，master 关全关）。
import { describe, expect, it } from 'vitest';
import { isP4SubOn, NEED02_P4_SUB_KEYS, readNeed02Flags, type Need02StorageLike } from './need02Flags';

function fakeStorage(records: Record<string, string> = {}): Need02StorageLike {
  return { getItem: (k: string) => (k in records ? records[k] : null) };
}

describe('need02-P4 子开关矩阵', () => {
  it('缺省全开（无 key → 四子项全 true）', () => {
    const flags = readNeed02Flags(fakeStorage());
    for (const sub of Object.keys(NEED02_P4_SUB_KEYS) as Array<'proj' | 'md' | 'reload' | 'window'>) {
      expect(isP4SubOn(sub, fakeStorage(), flags)).toBe(true);
    }
  });

  it('master p4=off → 子项恒关（全回旧行为）', () => {
    const storage = fakeStorage({ 'halo-need02-p4': 'off' });
    const flags = readNeed02Flags(storage);
    expect(flags.p4).toBe(false);
    for (const sub of Object.keys(NEED02_P4_SUB_KEYS) as Array<'proj' | 'md' | 'reload' | 'window'>) {
      expect(isP4SubOn(sub, storage, flags)).toBe(false);
    }
  });

  it('子项互相独立：只关 p4-md 不影响其余', () => {
    const storage = fakeStorage({ 'halo-need02-p4-md': 'off' });
    const flags = readNeed02Flags(storage);
    expect(isP4SubOn('md', storage, flags)).toBe(false);
    expect(isP4SubOn('proj', storage, flags)).toBe(true);
    expect(isP4SubOn('reload', storage, flags)).toBe(true);
    expect(isP4SubOn('window', storage, flags)).toBe(true);
  });

  it('大小写不敏感（OFF 同 off）', () => {
    const storage = fakeStorage({ 'halo-need02-p4-proj': 'OFF' });
    expect(isP4SubOn('proj', storage, readNeed02Flags(storage))).toBe(false);
  });

  it('无 flags 入参时自行读 storage（含 master 门控）', () => {
    expect(isP4SubOn('reload', fakeStorage())).toBe(true);
    expect(isP4SubOn('reload', fakeStorage({ 'halo-need02-p4': 'off' }))).toBe(false);
  });
});
