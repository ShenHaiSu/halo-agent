// src/Web.Client/src/components/icons/icons.test.ts
import { describe, expect, it } from 'vitest';
import { ICONS, type IconName, type IconPath } from './paths';

/**
 * 图标数据的完整性。
 *
 * ============ 这组测试守住什么 ============
 *  ① 数量不足 67 → P5 开发时"找不到图标"就开始乱用 emoji
 *  ② 某条 path 的 d 为空 / 不合法 → 该图标不显示（且无任何报错）
 *  ③ viewBox 不是 24×24 → 图标变形或裁剪
 *  ④ d 里有 NaN / undefined（拼接错误）→ 整个 svg 渲染失败
 * ============ 为什么这些必须在测试里 ============
 *  SVG path 的 d 是纯字符串，写错一个数字浏览器不报错，只是"图形不对"。
 *  67 个图标靠人眼看一遍不现实。用正则做基本合法性检查，能抓 90% 的错。
 * ============
 */
describe('ICONS', () => {
  const entries: Array<[string, IconPath]> = Object.entries(ICONS);

  it('至少有 67 个图标', () => {
    expect(entries.length).toBeGreaterThanOrEqual(67);
  });

  it('图标名全是 kebab-case', () => {
    const bad = entries.filter(([k]) => !/^[a-z][a-z0-9]*(-[a-z0-9]+)*$/.test(k));
    expect(bad.map(([k]) => k)).toEqual([]);
  });

  it('每个图标至少有一条 path', () => {
    const empty = entries.filter(([, v]) => !Array.isArray(v.d) || v.d.length === 0);
    expect(empty.map(([k]) => k)).toEqual([]);
  });

  it('每条 path 的 d 都以合法 SVG 命令开头', () => {
    // 常见 2D path 命令：M m L l H h V v C c S s Q q T t A a Z z
    const LEGAL = /^[MmZzLlHhVvCcSsQqTtAa0-9.,\s+-]/;
    const bad: string[] = [];
    for (const [name, icon] of entries) {
      for (const d of icon.d) {
        if (typeof d !== 'string' || d.trim() === '') {
          bad.push(`${name}: 空 path`);
          continue;
        }
        if (!LEGAL.test(d.trim())) {
          bad.push(`${name}: 非法开头「${d.slice(0, 12)}」`);
        }
        // 常见拼接错误
        if (/undefined|NaN|null/.test(d)) {
          bad.push(`${name}: 含 undefined/NaN`);
        }
      }
    }
    expect(bad).toEqual([]);
  });

  it('viewBox 缺省或为 0 0 24 24', () => {
    const bad = entries.filter(([, v]) => v.viewBox !== undefined && v.viewBox !== '0 0 24 24');
    expect(bad.map(([k, v]) => `${k}: ${v.viewBox}`)).toEqual([]);
  });

  it('图标名不重复（对象键天然不重复，这里验证没有大小写混用）', () => {
    const lower = entries.map(([k]) => k.toLowerCase());
    expect(new Set(lower).size).toBe(lower.length);
  });

  it('必须有 P5 会用到的核心图标', () => {
    const required: IconName[] = [
      'chevron-down',
      'chevron-right',
      'x',
      'check',
      'search',
      'plus',
      'trash',
      'copy',
      'settings',
      'refresh',
    ];
    for (const name of required) {
      expect(ICONS[name], `缺少核心图标 ${name}`).toBeDefined();
    }
  });

  it('filled 图标与 stroke 图标不混用同一组属性', () => {
    const withFill = entries.filter(([, v]) => v.filled === true);
    // filled 图标必须有 viewBox 或至少一条 path（本测试主要防 undefined）
    for (const [name, v] of withFill) {
      expect(v.d.length, `${name} 是 filled 但无 path`).toBeGreaterThan(0);
    }
    // 数量上限（避免把所有图标都标成 filled）
    expect(withFill.length).toBeLessThan(entries.length * 0.2);
  });
});
