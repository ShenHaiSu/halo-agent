// src/Web.Client/src/stores/eventBridge.ts
// P5：24 事件 → UI 唯一映射（I-1 铁律）。
import { useMessagesStore } from './messages';
import { useStreamStore } from './stream';
import { usePermissionsStore } from './permissions';
import { useSessionStore } from './session';
import { useQueueStore } from './queue';
import { useUiStore } from './ui';
import { transport } from '@/transport';
import type { HaloFrame } from '@/bridge/types';

type Frame = { t?: string; seq?: number; k: string; p?: unknown };

function asRecord(p: unknown): Record<string, unknown> {
  return (p ?? {}) as Record<string, unknown>;
}

function toMessageViews(list: unknown): Array<Record<string, unknown>> {
  if (!Array.isArray(list)) return [];
  return list.map((m) => {
    const o = (m ?? {}) as Record<string, unknown>;
    return {
      id: o.id,
      role: o.role ?? 'assistant',
      content: o.text ?? o.content ?? '',
      reasoning: o.reasoning ?? undefined,
      ts: o.tsMs ?? Date.now(),
      injected: o.injected ?? false,
      toolCalls: o.toolCalls ?? undefined,
    };
  });
}

export function dispatchFrames(frames: HaloFrame[]): void {
  if (!frames || frames.length === 0) return;
  const sorted = [...(frames as unknown as Frame[])].sort((a, b) => (a.seq ?? 0) - (b.seq ?? 0));
  const messages = useMessagesStore();
  for (const f of sorted) {
    if (f.t !== 'evt') continue;
    const seq = f.seq ?? 0;
    if (seq <= messages.lastSeq) {
      console.debug('[evt] 丢弃重复 seq', seq, f.k);
      continue;
    }
    try {
      switch (f.k) {
        case 'turn.started': {
          const p = asRecord(f.p);
          const turnId = String(p.turnId ?? `turn-${seq}`);
          const { useStreamStore: us } = { useStreamStore: undefined as never };
          void us;
          const stream = useStreamStore();
          const id = messages.append({ role: 'assistant', content: '' });
          messages.streamingId = id;
          stream.beginTurn(turnId, id);
          break;
        }
        case 'turn.finished': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          const id = messages.streamingId;
          if (id) {
            const truncated = p.truncated === true;
            const reason = typeof p.reason === 'string' ? p.reason : '';
            messages.patch(id, {
              streaming: false,
              truncatedByLength: truncated && reason !== 'user-abort',
              truncatedByUser: p.byUser === true || reason === 'user-abort',
            });
            if (typeof p.text === 'string' && p.text.length > 0) {
              const cur = messages.items.find((m) => m.id === id);
              if (cur && cur.content.length === 0) cur.content = p.text as string;
            }
          }
          stream.endTurn();
          messages.streamingId = null;
          break;
        }
        case 'text.delta': {
          const p = asRecord(f.p);
          const id = messages.streamingId;
          if (id && typeof p.text === 'string') messages.appendDelta(id, p.text as string);
          break;
        }
        case 'reasoning.delta': {
          const p = asRecord(f.p);
          const id = messages.streamingId;
          if (id && typeof p.text === 'string') messages.appendReasoning(id, p.text as string);
          break;
        }
        case 'tool.started': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          const id = messages.streamingId;
          const tcid = String(p.toolCallId ?? '');
          if (tcid && id) {
            messages.ensureToolCall(
              id,
              tcid,
              String(p.name ?? ''),
              String(p.args ?? p.arguments ?? '{}')
            );
            stream.addTool({
              toolCallId: tcid,
              messageId: id,
              name: String(p.name ?? ''),
              arguments: String(p.args ?? p.arguments ?? '{}'),
              startedAt: Date.now(),
              output: '',
              alertLevel: Number(p.idleMs ?? 0) > 30000 ? 1 : 0,
            });
          }
          break;
        }
        case 'tool.finished': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          const tcid = String(p.toolCallId ?? '');
          if (tcid) {
            const t = stream.activeTools.get(tcid);
            const msgId = t?.messageId ?? messages.streamingId;
            if (msgId) {
              messages.patchToolCall(msgId, tcid, {
                status: p.ok === false ? 'error' : 'done',
                result: typeof p.output === 'string' ? (p.output as string) : undefined,
                output: typeof p.output === 'string' ? (p.output as string) : undefined,
                ok: typeof p.ok === 'boolean' ? (p.ok as boolean) : true,
              });
            }
            stream.removeTool(tcid);
          }
          break;
        }
        case 'tool.outputDelta': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          const tcid = String(p.toolCallId ?? '');
          const t = stream.activeTools.get(tcid);
          if (t && typeof p.delta === 'string') {
            t.output += p.delta as string;
            messages.patchToolCall(t.messageId, tcid, { output: t.output, status: 'running' });
          }
          break;
        }
        case 'tool.silentAlert': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          const tcid = String(p.toolCallId ?? '');
          const level = Number(p.level ?? 1);
          stream.patchTool(tcid, { alertLevel: level });
          const t = stream.activeTools.get(tcid);
          if (t) messages.patchToolCall(t.messageId, tcid, { alertLevel: level });
          break;
        }
        case 'round.started': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          stream.round.used = Number(p.used ?? stream.round.used);
          stream.round.limit = Number(p.limit ?? stream.round.limit);
          break;
        }
        case 'llm.retrying': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          stream.retrying = {
            attempt: Number(p.attempt ?? 0),
            max: Number(p.max ?? 0),
            reason: String(p.reason ?? ''),
          };
          break;
        }
        case 'usage': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          const session = useSessionStore();
          stream.usage.lastPromptTokens = Number(p.prompt ?? 0);
          stream.usage.lastCompletionTokens = Number(p.completion ?? 0);
          stream.usage.dropped = Number((p as Record<string, unknown>).dropped ?? p.dropped ?? 0);
          session.addUsage(Number(p.prompt ?? 0), Number(p.completion ?? 0));
          break;
        }
        case 'notice': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          const level = String(p.level ?? 'info') as 'info' | 'success' | 'warn' | 'error';
          stream.notice = {
            level: level === 'success' || level === 'warn' || level === 'error' ? level : 'info',
            text: String(p.text ?? ''),
          };
          break;
        }
        case 'ask.permission': {
          const p = asRecord(f.p);
          const perms = usePermissionsStore();
          perms.setPending({
            askId: String(p.askId ?? ''),
            kind: String(p.kind ?? 'tool.shell'),
            title: String(p.title ?? '需要你的确认'),
            body: String(p.body ?? ''),
            warning: (p.warning as string | null) ?? null,
            allowAlways: p.allowAlways === true,
            used: typeof p.used === 'number' ? (p.used as number) : null,
          });
          break;
        }
        case 'ask.rounds': {
          const p = asRecord(f.p);
          const perms = usePermissionsStore();
          perms.setPending({
            askId: String(p.askId ?? ''),
            kind: 'agent.rounds',
            title: `已进行 ${String(p.used ?? '')} 轮，是否继续？`,
            body: `模型已连续执行 ${String(p.used ?? '')} 轮工具调用。继续可能产生大量改动。`,
            warning: null,
            allowAlways: false,
            used: typeof p.used === 'number' ? (p.used as number) : null,
          });
          break;
        }
        case 'session.info': {
          const p = asRecord(f.p);
          useSessionStore().applyInfo({ id: p.id, cwd: p.cwd, historyPath: p.historyPath });
          break;
        }
        case 'session.historyLoaded': {
          const p = asRecord(f.p);
          messages.applySnapshot({ seq, messages: toMessageViews(p.messages) });
          messages.setLastSeq(seq);
          break;
        }
        case 'session.contextCleared': {
          messages.clear();
          messages.setLastSeq(seq);
          useUiStore().toast.info('已清除上下文（历史文件保留）');
          break;
        }
        case 'session.compactionStarted': {
          useStreamStore().compactionStarted = true;
          useStreamStore().notice = { level: 'info', text: '正在压缩上下文…' };
          break;
        }
        case 'session.compacted': {
          const p = asRecord(f.p);
          const session = useSessionStore();
          const stream = useStreamStore();
          session.compactionSummary = String(p.summary ?? '');
          stream.compactionStarted = false;
          stream.notice = {
            level: 'info',
            text: `已压缩上下文，移除 ${String(p.removed ?? 0)} 条`,
          };
          break;
        }
        case 'user.injected': {
          const p = asRecord(f.p);
          messages.append({ role: 'user', content: String(p.text ?? ''), injected: true });
          break;
        }
        case 'state.personaChanged': {
          const p = asRecord(f.p);
          useSessionStore().persona = String(p.name ?? '');
          break;
        }
        case 'state.modeChanged': {
          const p = asRecord(f.p);
          const session = useSessionStore();
          if (p.mode) session.mode = String(p.mode) as typeof session.mode;
          break;
        }
        case 'state.busyChanged': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          stream.busy = p.busy === true;
          stream.queueLength = Number(p.queueLength ?? 0);
          try {
            useQueueStore().syncFromEngine(Number(p.queueLength ?? 0));
          } catch {
            /* queue 未挂载 */
          }
          break;
        }
        case 'resync.required': {
          const stream = useStreamStore();
          stream.resyncRequired = true;
          stream.notice = { level: 'warn', text: '界面与后台不同步，正在重新载入…' };
          void (async () => {
            try {
              const snap = await transport.request<{
                seq?: number;
                messages?: Array<Record<string, unknown>>;
                busy?: boolean;
                queue?: unknown[];
              }>('agent.resync', null);
              messages.applySnapshot({
                seq: typeof snap.seq === 'number' ? snap.seq : seq,
                messages: (snap.messages ?? []) as Array<Record<string, unknown>>,
              });
              useUiStore().collapsedTools.clear();
              stream.resyncRequired = false;
              stream.notice = { level: 'success', text: '已重新载入' };
            } catch {
              // 失败时黄条保留
            }
          })();
          break;
        }
        default: {
          console.debug('[evt] 未处理的事件类型', f.k, f.p);
        }
      }
    } catch (e) {
      console.error('[evt] 处理事件失败', f.k, e);
    }
    messages.setLastSeq(seq);
  }
}
