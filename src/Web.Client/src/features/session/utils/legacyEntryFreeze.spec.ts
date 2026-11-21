/** 主链退役冻结守卫：遗留投影入口已标废弃，禁止新增引用；新代码走摘要源。 */
import { describe, expect, it } from 'vitest';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(__dirname, '..', '..', '..', '..');
const SRC = join(ROOT, 'src');

function read(rel: string): string {
  return readFileSync(join(ROOT, rel), 'utf8');
}

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

const FREEZE_PHRASE = '主链退役中，禁止新增引用';

describe('legacyEntryPointsAreMarkedDeprecated', () => {
  it('遗留入口文件头含冻结语', () => {
    const guarded = [
      'src/features/session/utils/turnGroup.ts',
      'src/features/agent/composables/useTimelineGroups.ts',
      'src/features/session/utils/pendingUser.ts',
      'src/stores/workspaceStore.ts',
    ];
    for (const rel of guarded) {
      expect(read(rel), rel).toContain(FREEZE_PHRASE);
    }
  });

  it('主链不再新增旧投影引用（白名单外零新增）', () => {
    const allowed = new Set(
      [
        'src/features/agent/composables/useTimelineProjection.ts',
        'src/features/agent/composables/useTimelineRowActions.ts',
        'src/features/agent/components/UserTurnBlock.vue',
        'src/features/agent/components/UserTurnSummary.vue',
        'src/features/agent/components/userturn/UserTurnProcessBody.vue',
        'src/features/agent/utils/steadyReady.ts',
        'src/features/session/components/HistoryTocPanel.vue',
        'src/shared/utils/turnGroup.ts',
        'src/stores/workspaceStore.ts',
        'src/stores/agentWiring.ts',
        'src/features/agent/stores/useAgentTelemetry.ts',
        'src/features/agent/stores/model.ts',
      ].map((r) => join(ROOT, r).toLowerCase())
    );
    const offenders: string[] = [];
    for (const f of listFiles(SRC)) {
      const text = readFileSync(f, 'utf8');
      if (!text.includes("@features/session/utils/userTurn'") && !text.includes('@features/session/utils/userTurn"')) continue;
      if (allowed.has(f.toLowerCase())) continue;
      // 子会话只读链与旧单测豁免：单测文件不参与源码门（由 C4 统一删除/迁移）。
      if (f.toLowerCase().endsWith('.spec.ts')) continue;
      if (f.toLowerCase().includes('subhistoryview') || f.toLowerCase().includes('usetimelinegroups')) continue;
      offenders.push(f.slice(ROOT.length + 1));
    }
    // 当前基线：转接层与行组件仍在白名单内；白名单外出现即视为新增引用。
    expect(offenders, `新增旧投影引用: ${offenders.join(', ')}`).toEqual([]);
  });
});
