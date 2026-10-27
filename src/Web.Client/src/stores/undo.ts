// src/Web.Client/src/stores/undo.ts
// P5：撤销快照（容错归一化）。
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { transport } from '@/transport';

export interface SnapshotFileInfo {
  path: string;
  relPath: string;
  linesBefore: number;
  linesCurrent: number | null;
  change: 'created' | 'modified' | 'deleted';
}
export interface SnapshotInfo {
  index: number;
  ts: number;
  files: SnapshotFileInfo[];
  toolName?: string;
  available: boolean;
}

function shortPath(p: string): string {
  if (p.length <= 46) return p;
  const parts = p.split(/[\\/]/);
  if (parts.length <= 3) return p;
  const head = parts[0] ?? '';
  const tail = parts.slice(-2).join('\\');
  return `${head}\\…\\${tail}`;
}

export function normalizeSnapshot(raw: unknown, i: number): SnapshotInfo {
  const o = (raw ?? {}) as Record<string, unknown>;
  const rawFiles = Array.isArray(o['files']) ? o['files'] : [];
  const files: SnapshotFileInfo[] = rawFiles.map((f) => {
    if (typeof f === 'string') {
      return {
        path: f,
        relPath: shortPath(f),
        linesBefore: 0,
        linesCurrent: null,
        change: 'modified' as const,
      };
    }
    const x = (f ?? {}) as Record<string, unknown>;
    const path = String(x['path'] ?? '');
    return {
      path,
      relPath: typeof x['relPath'] === 'string' ? (x['relPath'] as string) : shortPath(path),
      linesBefore: Number(x['linesBefore'] ?? x['lines'] ?? 0),
      linesCurrent:
        x['linesCurrent'] === undefined || x['linesCurrent'] === null
          ? null
          : Number(x['linesCurrent']),
      change:
        x['change'] === 'created' || x['change'] === 'deleted'
          ? (x['change'] as 'created' | 'deleted')
          : 'modified',
    };
  });
  return {
    index: Number(o['index'] ?? i),
    ts: Number(o['ts'] ?? o['tsMs'] ?? Date.now()),
    files,
    toolName: typeof o['toolName'] === 'string' ? (o['toolName'] as string) : undefined,
    available: o['available'] !== false,
  };
}

const MAX_DISPLAY = 50;

export const useUndoStore = defineStore('undo', () => {
  const items = ref<SnapshotInfo[]>([]);
  const loading = ref(false);
  const rollingBack = ref(false);
  const error = ref<string | null>(null);
  const previewIndex = ref<number | null>(null);

  const total = computed(() => items.value.length);
  const shown = computed(() => items.value.slice(0, MAX_DISPLAY));
  const hidden = computed(() => Math.max(0, total.value - MAX_DISPLAY));
  const isEmpty = computed(() => !loading.value && items.value.length === 0);

  function sortDesc(): void {
    items.value.sort((a, b) => b.ts - a.ts);
  }

  async function load(): Promise<void> {
    loading.value = true;
    error.value = null;
    try {
      const r = await transport.request<{ items?: unknown[] }>('tool.undoList', null);
      const raw = Array.isArray(r.items) ? r.items : [];
      items.value = raw.map(normalizeSnapshot);
      sortDesc();
    } catch (e) {
      error.value = (e as Error).message;
      items.value = [];
    } finally {
      loading.value = false;
    }
  }

  async function rollback(index: number): Promise<{ ok: boolean; message?: string }> {
    if (rollingBack.value) return { ok: false, message: '正在恢复中' };
    rollingBack.value = true;
    try {
      const r = await transport.request<{ ok?: boolean; orig?: string }>('tool.undoRollback', {
        index,
      });
      if (r.ok ?? true) {
        await load();
        return { ok: true };
      }
      return { ok: false, message: r.orig ?? '恢复失败' };
    } catch (e) {
      return { ok: false, message: (e as Error).message };
    } finally {
      rollingBack.value = false;
    }
  }

  function togglePreview(index: number): void {
    previewIndex.value = previewIndex.value === index ? null : index;
  }
  function clear(): void {
    items.value = [];
    previewIndex.value = null;
    error.value = null;
  }

  return {
    items,
    shown,
    hidden,
    total,
    loading,
    rollingBack,
    error,
    previewIndex,
    isEmpty,
    maxDisplay: MAX_DISPLAY,
    load,
    rollback,
    togglePreview,
    clear,
  };
});
