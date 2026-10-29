import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { BEHAVIORS } from './behaviors';

const here = dirname(fileURLToPath(import.meta.url));
const schemaPath = resolve(here, '../../../../../contract/agent.schema.json');

describe('Mock 行为表与契约一致性', () => {
  const schema = JSON.parse(readFileSync(schemaPath, 'utf8')) as {
    methods: Record<string, { implementedIn?: string }>;
  };
  const contractMethods = Object.keys(schema.methods).filter(
    (m) => schema.methods[m]?.implementedIn !== 'host',
  );

  it('Mock 覆盖契约中所有 engine 方法', () => {
    const missing = contractMethods.filter((m) => !(m in BEHAVIORS));
    expect(missing).toEqual([]);
  });

  it('Mock 不含契约外的方法', () => {
    const extra = Object.keys(BEHAVIORS).filter((m) => !contractMethods.includes(m));
    expect(extra).toEqual([]);
  });

  it('tool.list / tool.undoList 返回 {items}', () => {
    expect(BEHAVIORS['tool.list']?.kind).toBe('ok');
    expect(BEHAVIORS['tool.undoList']?.kind).toBe('ok');
    if (BEHAVIORS['tool.list']?.kind === 'ok' && BEHAVIORS['tool.undoList']?.kind === 'ok') {
      const a = (BEHAVIORS['tool.list'] as { run: (p: unknown) => unknown }).run(null) as {
        items?: unknown;
      };
      const b = (BEHAVIORS['tool.undoList'] as { run: (p: unknown) => unknown }).run(null) as {
        items?: unknown;
      };
      expect(Array.isArray(a.items)).toBe(true);
      expect(Array.isArray(b.items)).toBe(true);
    }
  });

  it('settings.get Mock 含 providers 整树 + proxy', () => {
    const b = BEHAVIORS['settings.get'];
    expect(b?.kind).toBe('ok');
    if (b?.kind === 'ok') {
      const s = b.run(null) as { providers?: unknown[]; proxy?: { mode?: string }; activeProvider?: string };
      expect(s.providers).toBeDefined();
      expect(Array.isArray(s.providers)).toBe(true);
      expect(s.proxy?.mode).toBe('auto');
      expect(typeof s.activeProvider).toBe('string');
    }
  });

  it('update.check 形状符合契约', () => {
    const b = BEHAVIORS['update.check'];
    if (b?.kind === 'ok') {
      const r = b.run(null) as { available?: boolean; latestVersion?: unknown; notes?: unknown };
      expect(typeof r.available).toBe('boolean');
      expect('latestVersion' in r).toBe(true);
      expect('notes' in r).toBe(true);
      expect('configured' in r).toBe(false);
    }
  });

  it('state.setPersona 返回 {name} 而非 {persona}', () => {
    const b = BEHAVIORS['state.setPersona'];
    if (b?.kind === 'ok') {
      const r = b.run({ name: 'x' }) as Record<string, unknown>;
      expect(r).toHaveProperty('name');
      expect(r).not.toHaveProperty('persona');
    }
  });
});
