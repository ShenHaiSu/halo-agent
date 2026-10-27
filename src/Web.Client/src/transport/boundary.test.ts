import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

function walk(dir: string, out: string[] = []): string[] {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) walk(p, out);
    else if (/\.(ts|vue)$/.test(name) && !name.endsWith('.test.ts')) out.push(p);
  }
  return out;
}

describe('transport 边界守卫', () => {
  it('除桥封装本体外，不得直接访问 window.haloAgent / window.haloHost', () => {
    const here = dirname(fileURLToPath(import.meta.url));
    const root = resolve(here, '..');
    const offenders: string[] = [];

    for (const file of walk(root)) {
      const rel = file.replace(/\\/g, '/').split('/src/').pop() ?? '';
      if (rel.startsWith('bridge/') || rel.startsWith('transport/')) continue;

      const src = readFileSync(file, 'utf8');
      const cleaned = src
        .split('\n')
        .filter((l) => !l.includes('eslint-disable'))
        .join('\n');

      if (/window\.haloAgent/.test(cleaned) || /window\.haloHost/.test(cleaned)) {
        offenders.push(rel);
      }
    }

    expect(offenders).toEqual([]);
  });
});
