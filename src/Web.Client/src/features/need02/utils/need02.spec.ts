/**
 * 渲染开关矩阵单测。
 *
 * 覆盖：
 * 1. 缺省全开（空 storage / 无 key → p1/p3/p4 全 true）；
 * 2. 'off' 关闭（大小写不敏感），其余任意值保持开启；
 * 3. 三开关互相独立；
 * 4. storage 抛错（隐私模式）→ 回落默认值全开；
 * 5. data-need02 透传串格式。
 */
import { describe, expect, it } from 'vitest';
import { NEED02_FLAG_KEYS, need02Attr, readNeed02Flags, readP02Flags, type Need02StorageLike } from '@features/need02/utils/need02Flags';

function fakeStorage(entries: Record<string, string> = {}): Need02StorageLike {
  return {
    getItem: (key: string): string | null => (key in entries ? entries[key] : null),
  };
}

function throwingStorage(): Need02StorageLike {
  return {
    getItem: (): string | null => {
      throw new Error('denied');
    },
  };
}

describe('readNeed02Flags 缺省', () => {
  it('空 storage → p1/p3/p4 全开', () => {
    expect(readNeed02Flags(fakeStorage())).toEqual({ p1: true, p3: true, p4: true });
  });

  it('null storage（无 window / SSR）→ 全开', () => {
    expect(readNeed02Flags(null)).toEqual({ p1: true, p3: true, p4: true });
  });

  it('抛错 storage（隐私模式）→ 回落全开', () => {
    expect(readNeed02Flags(throwingStorage())).toEqual({ p1: true, p3: true, p4: true });
  });
});

describe('readNeed02Flags 开关语义', () => {
  it("'off' 关闭（大小写不敏感）", () => {
    const flags = readNeed02Flags(
      fakeStorage({ [NEED02_FLAG_KEYS.p1]: 'off', [NEED02_FLAG_KEYS.p3]: 'OFF', [NEED02_FLAG_KEYS.p4]: 'Off' })
    );
    expect(flags.p1).toBe(false);
    expect(flags.p3).toBe(false);
    expect(flags.p4).toBe(false);
  });

  it('非 off 任意值（on/0/1/yes/typo）→ 保持开启', () => {
    const flags = readNeed02Flags(
      fakeStorage({
        [NEED02_FLAG_KEYS.p1]: 'on',
        [NEED02_FLAG_KEYS.p3]: '0',
        [NEED02_FLAG_KEYS.p4]: 'oFFf',
      })
    );
    expect(flags).toEqual({ p1: true, p3: true, p4: true });
  });

  it('三开关互相独立：只关 p3 不影响 p1/p4', () => {
    const flags = readNeed02Flags(fakeStorage({ [NEED02_FLAG_KEYS.p3]: 'off' }));
    expect(flags).toEqual({ p1: true, p3: false, p4: true });
  });

  it('三键全 off', () => {
    const flags = readNeed02Flags(
      fakeStorage({
        [NEED02_FLAG_KEYS.p1]: 'off',
        [NEED02_FLAG_KEYS.p3]: 'off',
        [NEED02_FLAG_KEYS.p4]: 'off',
      })
    );
    expect(flags).toEqual({ p1: false, p3: false, p4: false });
  });
});

describe('need02Attr 透传串', () => {
  it('全开', () => {
    expect(need02Attr({ p1: true, p3: true, p4: true })).toBe('p1:on,p3:on,p4:on');
  });

  it('全关', () => {
    expect(need02Attr({ p1: false, p3: false, p4: false })).toBe('p1:off,p3:off,p4:off');
  });

  it('混合', () => {
    expect(need02Attr({ p1: true, p3: false, p4: true })).toBe('p1:on,p3:off,p4:on');
  });
});

describe('P02 readP02Flags 单开关', () => {
  it('缺省开', () => {
    expect(readP02Flags(fakeStorage())).toEqual({ singleProcess: true });
  });

  it('显式 off 关闭', () => {
    expect(readP02Flags(fakeStorage({ 'halo-single-process': 'off' })).singleProcess).toBe(false);
  });

  it('storage 抛错降级为关', () => {
    expect(readP02Flags(throwingStorage())).toEqual({ singleProcess: false });
  });
});
