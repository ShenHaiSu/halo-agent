<!-- src/Web.Client/src/components/common/CommandPalette.vue -->
<!-- Ctrl+K 命令面板（01 §11.4 + 05 §6）：会话 + 命令 + 技能 + 人格 + 设置项，本地过滤上限 40。 -->
<script lang="ts">
import { ref as sharedRef } from 'vue';

const paletteOpen = sharedRef(false);

/** 跨组件打开/切换命令面板。 */
export function openCommandPalette(): void {
  paletteOpen.value = true;
}
export function toggleCommandPalette(): void {
  paletteOpen.value = !paletteOpen.value;
}
export function useCommandPaletteOpen() {
  return paletteOpen;
}
</script>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useSessionsStore } from '@/stores/sessions';
import { useSessionStore } from '@/stores/session';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import { fuzzyScore, highlight } from './fuzzy';
import { openSettings, type SettingsTab } from '@/components/settings/SettingsApp.vue';
import { openShortcutHelp } from './ShortcutHelp.vue';
import HDialog from '@/components/base/HDialog.vue';
import HInput from '@/components/base/HInput.vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';

const sessions = useSessionsStore();
const current = useSessionStore();
const toast = useToast();

const query = ref('');
const active = ref(0);
const inputEl = ref<InstanceType<typeof HInput> | null>(null);

/** 最近使用（内存，不持久化） */
const recent = ref<string[]>([]);
const skillNames = ref<string[]>([]);
const personaNames = ref<string[]>([]);

interface Entry {
  group: string;
  label: string;
  hint?: string;
  icon:
    | 'plus'
    | 'settings'
    | 'shield-check'
    | 'layers'
    | 'history'
    | 'palette'
    | 'info'
    | 'message'
    | 'user'
    | 'zap'
    | 'terminal';
  id: string;
  run: () => void | Promise<void>;
}

function close(): void {
  paletteOpen.value = false;
}

async function loadResources(): Promise<void> {
  try {
    const s = (await transport.request('asset.skills.list', null)) as {
      items?: Array<{ name?: string }>;
    };
    skillNames.value = (s.items ?? []).map((x) => x?.name ?? '').filter((x) => x !== '');
  } catch {
    skillNames.value = [];
  }
  try {
    const p = (await transport.request('asset.personas.list', null)) as { items?: string[] };
    personaNames.value = Array.isArray(p.items) ? p.items : [];
  } catch {
    personaNames.value = [];
  }
}

function gotoSettings(tab: SettingsTab): void {
  openSettings(tab);
  close();
}

function commands(): Entry[] {
  return [
    {
      group: '命令',
      id: 'cmd:new',
      label: '新建对话',
      hint: 'Ctrl+N',
      icon: 'plus',
      run: async () => {
        await sessions.createNew();
        close();
      },
    },
    {
      group: '命令',
      id: 'cmd:settings',
      label: '打开设置',
      hint: 'Ctrl+,',
      icon: 'settings',
      run: () => gotoSettings('general'),
    },
    {
      group: '命令',
      id: 'cmd:model',
      label: '打开模型设置',
      icon: 'zap',
      run: () => gotoSettings('model'),
    },
    {
      group: '命令',
      id: 'cmd:mode',
      label: '切换权限模式',
      icon: 'shield-check',
      run: () => gotoSettings('permissions'),
    },
    {
      group: '命令',
      id: 'cmd:compact',
      label: '压缩上下文',
      hint: '/compact',
      icon: 'layers',
      run: async () => {
        await transport.request('session.compact', null);
        close();
      },
    },
    {
      group: '命令',
      id: 'cmd:mcp',
      label: '打开 MCP 扩展设置',
      icon: 'layers',
      run: () => gotoSettings('mcp'),
    },
    {
      group: '命令',
      id: 'cmd:theme',
      label: '切换主题',
      icon: 'palette',
      run: () => {
        const order = ['light', 'dark', 'midnight', 'system'];
        const idx = order.indexOf(document.documentElement.dataset['theme'] ?? 'light');
        const next = order[(idx + 1) % order.length] ?? 'light';
        document.documentElement.dataset['theme'] = next;
        try {
          localStorage.setItem('haloagent.theme', next);
        } catch {
          /* 忽略 */
        }
        close();
      },
    },
    {
      group: '命令',
      id: 'cmd:diag',
      label: '复制诊断信息',
      hint: '/diagnostics',
      icon: 'info',
      run: async () => {
        const d = await transport.request<unknown>('system.diagnostics', null);
        const { hostBridge } = await import('@/bridge/host');
        try {
          await hostBridge.copyText(JSON.stringify(d, null, 2));
          toast.success('诊断信息已复制');
        } catch {
          toast.error('复制失败');
        }
        close();
      },
    },
    {
      group: '命令',
      id: 'cmd:help',
      label: '快捷键帮助',
      hint: 'Ctrl+/',
      icon: 'terminal',
      run: () => {
        openShortcutHelp();
        close();
      },
    },
  ];
}

interface Scored {
  entry: Entry;
  score: number;
  indices: number[];
}

const result = computed<{ groups: Array<[string, Scored[]]>; total: number }>(() => {
  const q = query.value.trim();

  const all: Entry[] = [
    ...sessions.list.map<Entry>((s) => ({
      group: '会话',
      id: `sess:${s.id}`,
      label: s.title,
      hint: sessions.relativeTimeOf(s),
      icon: 'message',
      run: async () => {
        await sessions.select(s.id);
        close();
      },
    })),
    ...commands(),
    ...skillNames.value.map<Entry>((n) => ({
      group: '技能',
      id: `skill:${n}`,
      label: n,
      icon: 'zap',
      run: () => gotoSettings('assets'),
    })),
    ...personaNames.value.map<Entry>((n) => ({
      group: '人格',
      id: `persona:${n}`,
      label: n,
      icon: 'user',
      run: async () => {
        await current.setPersona(n);
        toast.success(`已切换人格：${n}`);
        close();
      },
    })),
    ...(
      [
        ['general', '设置：常规'],
        ['model', '设置：模型'],
        ['network', '设置：网络'],
        ['permissions', '设置：权限'],
        ['mcp', '设置：扩展'],
        ['assets', '设置：资源'],
        ['about', '设置：关于'],
      ] as Array<[SettingsTab, string]>
    ).map<Entry>(([t, label]) => ({
      group: '设置项',
      id: `settings:${t}`,
      label,
      icon: 'settings',
      run: () => gotoSettings(t),
    })),
  ];

  const scored: Scored[] = [];
  for (const e of all) {
    if (!q) {
      const isCmd = e.group === '命令';
      if (isCmd) {
        scored.push({
          entry: e,
          score: 500 + (recent.value.includes(e.id) ? 100 : 0),
          indices: [],
        });
      } else if (sessions.list.slice(0, 15).some((s) => `sess:${s.id}` === e.id)) {
        scored.push({ entry: e, score: 100, indices: [] });
      }
      continue;
    }
    const fs = fuzzyScore(e.label, q);
    if (!fs) continue;
    scored.push({
      entry: e,
      score: fs.score + (recent.value.includes(e.id) ? 50 : 0),
      indices: fs.indices,
    });
  }

  scored.sort((a, b) => b.score - a.score);
  const top = scored.slice(0, 40);

  const map = new Map<string, Scored[]>();
  for (const s of top) {
    const arr = map.get(s.entry.group) ?? [];
    arr.push(s);
    map.set(s.entry.group, arr);
  }
  const order = ['命令', '会话', '技能', '人格', '设置项'];
  const groups = order.filter((g) => map.has(g)).map((g) => [g, map.get(g)!] as [string, Scored[]]);

  return { groups, total: top.length };
});

const flat = computed<Scored[]>(() => result.value.groups.flatMap(([, list]) => list));

function isActive(id: string): boolean {
  return flat.value[active.value]?.entry.id === id;
}

watch(query, () => {
  active.value = 0;
});

watch(paletteOpen, (open) => {
  if (open) {
    query.value = '';
    active.value = 0;
    void loadResources();
    void nextTick(() => inputEl.value?.focus?.());
  }
});

function move(d: number): void {
  const n = flat.value.length;
  if (n === 0) return;
  active.value = (active.value + d + n) % n;
}

function run(): void {
  const e = flat.value[active.value];
  if (!e) return;
  recent.value = [e.entry.id, ...recent.value.filter((x) => x !== e.entry.id)].slice(0, 12);
  void e.entry.run();
}

function onKeydown(e: KeyboardEvent): void {
  if (e.isComposing) return;
  if (e.key === 'ArrowDown') {
    e.preventDefault();
    move(1);
    return;
  }
  if (e.key === 'ArrowUp') {
    e.preventDefault();
    move(-1);
    return;
  }
  if (e.key === 'Enter') {
    e.preventDefault();
    run();
  }
}

function onDocKeydown(e: KeyboardEvent): void {
  if (e.isComposing) return;
  const mod = e.ctrlKey || e.metaKey;
  if (mod && e.key.toLowerCase() === 'k') {
    e.preventDefault();
    paletteOpen.value = !paletteOpen.value;
  }
}

onMounted(() => document.addEventListener('keydown', onDocKeydown, { capture: true }));
onBeforeUnmount(() => document.removeEventListener('keydown', onDocKeydown, { capture: true }));
</script>

<template>
  <HDialog :open="paletteOpen" title="命令面板" width="lg" @update:open="paletteOpen = $event">
    <div class="palette">
      <HInput
        ref="inputEl"
        v-model="query"
        placeholder="搜索会话或命令…"
        size="md"
        @keydown="onKeydown"
      />

      <div class="palette-list" role="listbox" aria-label="搜索结果">
        <div v-if="flat.length === 0" class="palette-empty">
          {{ query ? `没有匹配「${query}」的结果` : '没有可显示的条目' }}
        </div>

        <template v-for="[group, list] in result.groups" :key="group">
          <div class="palette-group">{{ group }}</div>
          <button
            v-for="s in list"
            :key="s.entry.id"
            class="palette-item"
            :class="{ active: isActive(s.entry.id) }"
            type="button"
            role="option"
            :aria-selected="isActive(s.entry.id)"
            @click="run()"
            @mouseenter="active = flat.findIndex((x) => x.entry.id === s.entry.id)"
          >
            <HaloIcon :name="s.entry.icon" :size="14" />
            <span class="pi-label">
              <template v-for="(seg, i) in highlight(s.entry.label, s.indices)" :key="i">
                <mark v-if="seg.hit">{{ seg.text }}</mark>
                <span v-else>{{ seg.text }}</span>
              </template>
            </span>
            <span v-if="s.entry.hint" class="pi-hint">{{ s.entry.hint }}</span>
          </button>
        </template>
      </div>

      <div class="palette-foot">
        <span class="foot-hint">↑↓ 选择 · Enter 执行 · Esc 关闭</span>
        <span class="spacer" />
        <span class="foot-count">{{ flat.length }} 项</span>
      </div>
    </div>
  </HDialog>
</template>

<style scoped>
.palette {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  min-height: 0;
}
.palette-list {
  max-height: 420px;
  overflow-y: auto;
  margin: 0 calc(-1 * var(--space-2));
  min-height: 120px;
}
.palette-group {
  padding: var(--space-1) var(--space-3);
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  letter-spacing: 0.04em;
}
.palette-item {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  width: 100%;
  padding: var(--space-2) var(--space-3);
  background: transparent;
  border: 0;
  cursor: pointer;
  text-align: left;
  color: hsl(var(--fg));
  font-size: var(--text-sm);
  min-width: 0;
}
.palette-item:hover {
  background: hsl(var(--bg-subtle));
}
.palette-item.active {
  background: hsl(var(--bg-muted));
}
.pi-label {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.pi-label mark {
  background: hsl(var(--warning-subtle));
  color: hsl(var(--fg));
  border-radius: 2px;
}
.pi-hint {
  flex: none;
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  font-family: var(--font-mono);
}
.palette-empty {
  padding: var(--space-4);
  text-align: center;
  color: hsl(var(--fg-muted));
  font-size: var(--text-sm);
}
.palette-foot {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding-top: var(--space-1);
  border-top: 1px solid hsl(var(--border-subtle));
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
}
.spacer {
  flex: 1;
}
.foot-hint {
  font-family: var(--font-mono);
}
.foot-count {
  font-family: var(--font-mono);
}
</style>
