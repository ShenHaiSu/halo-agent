/**
 * need02-P0：开关矩阵单测（docs/need-02/04-P0 §改动清单）。
 *
 * 覆盖：
 * 1. 缺省全开（空 storage / 无 key → p1–p4 全 true）；
 * 2. 'off' 关闭（大小写不敏感），其余任意值保持开启；
 * 3. 四开关互相独立；
 * 4. storage 抛错（隐私模式）→ 回落默认值全开；
 * 5. 投影分支选择：p2 on 走 user-turn（自动旁路 halo-turn-run）；p2 off 回落旧链；
 * 6. data-need02 透传串格式（P0 截图对比锚点）。
 */
import { describe, expect, it } from 'vitest';
import {
  NEED02_FLAG_KEYS,
  need02Attr,
  readNeed02Flags,
  resolveTimelineMode,
  type Need02StorageLike,
} from '@features/need02/utils/need02Flags';

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
  it('空 storage → p1–p4 全开', () => {
    expect(readNeed02Flags(fakeStorage())).toEqual({ p1: true, p2: true, p3: true, p4: true });
  });

  it('null storage（无 window / SSR）→ 全开', () => {
    expect(readNeed02Flags(null)).toEqual({ p1: true, p2: true, p3: true, p4: true });
  });

  it('抛错 storage（隐私模式）→ 回落全开', () => {
    expect(readNeed02Flags(throwingStorage())).toEqual({ p1: true, p2: true, p3: true, p4: true });
  });
});

describe('readNeed02Flags 开关语义', () => {
  it("'off' 关闭（大小写不敏感）", () => {
    const flags = readNeed02Flags(
      fakeStorage({ [NEED02_FLAG_KEYS.p1]: 'off', [NEED02_FLAG_KEYS.p2]: 'OFF', [NEED02_FLAG_KEYS.p3]: 'Off' })
    );
    expect(flags.p1).toBe(false);
    expect(flags.p2).toBe(false);
    expect(flags.p3).toBe(false);
    expect(flags.p4).toBe(true);
  });

  it('非 off 任意值（on/0/1/yes/typo）→ 保持开启', () => {
    const flags = readNeed02Flags(
      fakeStorage({
        [NEED02_FLAG_KEYS.p1]: 'on',
        [NEED02_FLAG_KEYS.p2]: '0',
        [NEED02_FLAG_KEYS.p3]: 'yes',
        [NEED02_FLAG_KEYS.p4]: 'oFFf',
      })
    );
    expect(flags).toEqual({ p1: true, p2: true, p3: true, p4: true });
  });

  it('四开关互相独立：只关 p2 不影响 p1/p3/p4', () => {
    const flags = readNeed02Flags(fakeStorage({ [NEED02_FLAG_KEYS.p2]: 'off' }));
    expect(flags).toEqual({ p1: true, p2: false, p3: true, p4: true });
  });

  it('四键全 off', () => {
    const flags = readNeed02Flags(
      fakeStorage({
        [NEED02_FLAG_KEYS.p1]: 'off',
        [NEED02_FLAG_KEYS.p2]: 'off',
        [NEED02_FLAG_KEYS.p3]: 'off',
        [NEED02_FLAG_KEYS.p4]: 'off',
      })
    );
    expect(flags).toEqual({ p1: false, p2: false, p3: false, p4: false });
  });
});

describe('resolveTimelineMode 投影分支', () => {
  it('p2 on → user-turn（无视 halo-turn-run 旧开关：自动旁路）', () => {
    expect(resolveTimelineMode({ p1: true, p2: true, p3: true, p4: true }, false)).toBe('user-turn');
    expect(resolveTimelineMode({ p1: false, p2: true, p3: false, p4: false }, true)).toBe('user-turn');
  });

  it('p2 off + 旧开关未关 → legacy-turn-run（need-01 行为）', () => {
    expect(resolveTimelineMode({ p1: true, p2: false, p3: true, p4: true }, false)).toBe('legacy-turn-run');
  });

  it('p2 off + halo-turn-run=off → legacy-turn（改动前行为）', () => {
    expect(resolveTimelineMode({ p1: true, p2: false, p3: true, p4: true }, true)).toBe('legacy-turn');
  });

  it('p1/p3/p4 不影响投影分支选择', () => {
    const base = { p2: false, p1: true, p3: true, p4: true };
    expect(resolveTimelineMode(base, false)).toBe('legacy-turn-run');
    expect(resolveTimelineMode({ ...base, p1: false }, false)).toBe('legacy-turn-run');
    expect(resolveTimelineMode({ ...base, p3: false, p4: false }, false)).toBe('legacy-turn-run');
  });
});

describe('need02Attr 透传串', () => {
  it('全开', () => {
    expect(need02Attr({ p1: true, p2: true, p3: true, p4: true })).toBe('p1:on,p2:on,p3:on,p4:on');
  });

  it('全关（P0 验收门：data-need02 全 off 时与基线逐像素一致）', () => {
    expect(need02Attr({ p1: false, p2: false, p3: false, p4: false })).toBe('p1:off,p2:off,p3:off,p4:off');
  });

  it('混合', () => {
    expect(need02Attr({ p1: true, p2: false, p3: true, p4: false })).toBe('p1:on,p2:off,p3:on,p4:off');
  });
});
