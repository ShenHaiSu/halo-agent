/** 主链退役完成守卫：V1 投影整组已删除，禁止回潮；子会话只读链豁免。 */
import { describe, expect, it } from 'vitest';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(__dirname, '..', '..', '..', '..');
const SRC = join(ROOT, 'src');

function listFiles(dir: string, out: string[] = []): string[] {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    const st = statSync(p);
    if (st.isDirectory()) {
      if (name === 'node_modules' || name === 'dist') continue;
      listFiles(p, out);
    } else if (/\.(ts|vue)$/.test(name) && !name.endsWith('.spec.ts')) {
      out.push(p);
    }
  }
  return out;
}

describe('noLegacyProjectionSymbolsRemain', () => {
  it('主链无旧投影符号残留（子会话豁免路径除外）', () => {
    const offenders: string[] = [];
    for (const f of listFiles(SRC)) {
      const lower = f.toLowerCase();
      if (lower.includes('subhistoryview') || lower.includes('usetimelinegroups') || lower.includes('subsession')) continue;
      if (lower.endsWith('historytocpanel.vue') || lower.endsWith('historytoc.ts')) continue;
      if (lower.endsWith('stores/useagentelemetry.ts')) continue;
      if (lower.endsWith('session/utils/pendinguser.ts') || lower.endsWith('stores/workspacestore.ts')) continue;
      if (lower.endsWith('stores/agentwiring.ts') || lower.endsWith('stores/runtime.ts')) continue;
      const text = readFileSync(f, 'utf8');
      if (text.includes('buildUserTurns') || text.includes('buildUserTurnsIncremental') || text.includes('createUserTurnsCache')) {
        offenders.push(`${f.slice(ROOT.length + 1)}:buildUserTurns`);
      }
      if (text.includes('toPendingMessage(')) {
        offenders.push(`${f.slice(ROOT.length + 1)}:toPending`);
      }
      // 旧启发式对账已删除，仅主链新增引用才算回潮。
      if (text.includes('pendingOverlayMessages')) {
        offenders.push(`${f.slice(ROOT.length + 1)}:overlay`);
      }
      if (text.includes('activeUserTurnEntries') || text.includes('buildTimelineItems')) {
        offenders.push(`${f.slice(ROOT.length + 1)}:directory`);
      }
      if (text.includes('TranscriptHistory') || text.includes('TranscriptTail')) {
        offenders.push(`${f.slice(ROOT.length + 1)}:transcript`);
      }
      if (text.includes('hideLegacyHistory')) {
        offenders.push(`${f.slice(ROOT.length + 1)}:switch`);
      }
    }
    expect(offenders, `旧投影残留: ${offenders.join(', ')}`).toEqual([]);
  });
});
