// src/Web.Client/src/stores/eventBridge.ts
// P15：按 (used,kind) 路由 + 轮级搬运 + 双草稿 + 三态折叠。
import { normalizeToolCalls, useMessagesStore } from './messages';
import { clearCollapsedTools } from '@/components/chat/toolMeta';
import { useStreamStore } from './stream';
import { usePermissionsStore } from './permissions';
import { useSessionStore } from './session';
import { useQueueStore } from './queue';
import { useUiStore } from './ui';
import { isSwitching } from './sessions';
import { transport } from '@/transport';
import type { HaloFrame } from '@/bridge/types';

type Frame = { t?: string; seq?: number; k: string; p?: unknown };

function asRecord(p: unknown): Record<string, unknown> {
  return (p ?? {}) as Record<string, unknown>;
}

export function toMessageViews(list: unknown): Array<Record<string, unknown>> {
  if (!Array.isArray(list)) return [];
  const raw = list as Array<Record<string, unknown>>;
  const out: Array<Record<string, unknown>> = [];
  for (const m of raw) {
    const o = (m ?? {}) as Record<string, unknown>;
    out.push({
      turnId: o.turnId,
      user: o.user,
      injected: o.injected,
      process: o.process,
      final: o.final,
      id: o.turnId,
      role: 'assistant',
      content: '',
      text: '',
    });
  }
  return out;
}
// P09-fix2 B/C：切换中到达的历史变更帧一律丢弃（仍推进游标，避免 pump 反复拉取）。
// 只保留会话元信息 / 状态类帧（session.info/usage/state.*/resync 等）。
const DROP_DURING_SWITCH = new Set([
  'turn.started',
  'turn.finished',
  'text.delta',
  'reasoning.delta',
  'tool.started',
  'tool.finished',
  'tool.outputDelta',
  'tool.silentAlert',
  'round.started',
  'round.ended',
  'user.injected',
  'session.historyLoaded',
  'session.contextCleared',
]);

export function dispatchFrames(frames: HaloFrame[]): void {
  if (!frames || frames.length === 0) return;
  const sorted = [...(frames as unknown as Frame[])].sort((a, b) => (a.seq ?? 0) - (b.seq ?? 0));
  const messages = useMessagesStore();
  const switching = isSwitching();
  for (const f of sorted) {
    if (f.t !== 'evt') continue;
    const seq = f.seq ?? 0;
    if (seq <= messages.lastSeq) {
      console.debug('[evt] 丢弃重复 seq', seq, f.k);
      continue;
    }
    if (switching && DROP_DURING_SWITCH.has(f.k)) {
      console.debug('[evt] 切换中丢弃过期帧', seq, f.k);
      messages.setLastSeq(seq);
      continue;
    }
    try {
      switch (f.k) {
        case 'turn.started': {
          const p = asRecord(f.p);
          const turnId = String(p.turnId ?? `turn-${seq}`);
          const stream = useStreamStore();
          messages.ensureTurn(turnId);
          // 乐观 user 可能已占位 t-local：若只有一条 t-local 且内容为空则替换？简化：保留两者，Snapshot 会纠正
          stream.beginTurn(turnId);
          break;
        }
        case 'round.started': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          stream.round.used = Number(p.used ?? stream.round.used);
          stream.round.limit = Number(p.limit ?? stream.round.limit);
          const tid = stream.currentTurn?.turnId;
          if (tid) {
            stream.setActiveOrd(Number(p.used ?? 1));
            messages.ensureTurn(tid);
          }
          break;
        }
        case 'round.ended': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          const tid = stream.currentTurn?.turnId;
          const used = Number(p.used ?? 0);
          if (!tid || used <= 0) {
            stream.mismatch++;
            messages.mismatch++;
            console.warn('[turncard] round.ended 缺 used/turn，计数', p);
            break;
          }
          if (p.hasTools === true) {
            messages.moveRoundToProcess(tid, used);
          } else {
            // 末轮 ended（可省）：清该 used 草稿，等待 turn.finished
            messages.clearDraft(tid, used);
          }
          break;
        }
        case 'turn.finished': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          const turnId = String(p.turnId ?? stream.currentTurn?.turnId ?? '');
          if (!turnId) break;
          const finalUsed = Number(p.finalUsed ?? 0);
          if (!(finalUsed >= 1)) {
            stream.mismatch++;
            messages.mismatch++;
            console.warn('[turncard] turn.finished 缺 finalUsed，计数', p);
          }
          const text = typeof p.text === 'string' ? (p.text as string) : '';
          const truncated = p.truncated === true;
          const responseId = typeof p.responseId === 'string' ? (p.responseId as string) : undefined;
          messages.finalizeTurn(turnId, finalUsed >= 1 ? finalUsed : 1, text, truncated, responseId);
          // truncated 回合 auto 保持展开直到手动收：不写 userClosed，TurnCard computed 处理
          stream.endTurn();
          break;
        }
        case 'text.delta': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          const tid = stream.currentTurn?.turnId;
          const used = Number(p.used ?? 0);
          if (!tid || !(used >= 1)) {
            stream.mismatch++;
            messages.mismatch++;
            console.warn('[turncard] text.delta 非法 used，计数', p);
            break;
          }
          stream.setActiveOrd(used);
          if (typeof p.text === 'string') messages.appendDraft(tid, used, 'text', p.text as string);
          break;
        }
        case 'reasoning.delta': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          const tid = stream.currentTurn?.turnId;
          const used = Number(p.used ?? 0);
          if (!tid || !(used >= 1)) {
            stream.mismatch++;
            messages.mismatch++;
            console.warn('[turncard] reasoning.delta 非法 used，计数', p);
            break;
          }
          stream.setActiveOrd(used);
          if (typeof p.text === 'string') messages.appendDraft(tid, used, 'reasoning', p.text as string);
          break;
        }
        case 'tool.started': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          const tid = stream.currentTurn?.turnId;
          if (!tid) break;
          const tcid = String(p.toolCallId ?? '');
          const kind = p.kind === 'unknown' ? 'unknown' : 'function';
          const rawType = typeof p.rawType === 'string' ? (p.rawType as string) : undefined;
          const used = Number(p.used ?? stream.currentTurn?.activeOrd ?? 1);
          if (tcid) {
            messages.ensureToolCall(
              tid,
              used >= 1 ? used : 1,
              tcid,
              String(p.name ?? ''),
              String(p.args ?? p.arguments ?? '{}'),
              kind,
              rawType
            );
            // 同步 totals/truncated（头部推送）
            messages.patchToolCall(tid, tcid, {
              argsTotal: typeof p.argsTotal === 'number' ? (p.argsTotal as number) : undefined,
              truncated: p.truncated === true,
              used: used >= 1 ? used : 1,
            });
            stream.addTool({
              toolCallId: tcid,
              messageId: tid,
              name: String(p.name ?? ''),
              arguments: String(p.args ?? p.arguments ?? '{}'),
              startedAt: Date.now(),
              output: '',
              alertLevel: Number(p.idleMs ?? 0) > 30000 ? 1 : 0,
              kind,
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
            const tid = t?.messageId ?? stream.currentTurn?.turnId;
            const used = Number(p.used ?? stream.currentTurn?.activeOrd ?? 1);
            if (tid) {
              messages.patchToolCall(tid, tcid, {
                status: p.ok === false ? 'error' : 'done',
                result: typeof p.output === 'string' ? (p.output as string) : undefined,
                output: typeof p.output === 'string' ? (p.output as string) : undefined,
                ok: typeof p.ok === 'boolean' ? (p.ok as boolean) : true,
                outputTotal: typeof p.outputTotal === 'number' ? (p.outputTotal as number) : undefined,
                truncated: p.truncated === true,
                used: used >= 1 ? used : 1,
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
        case 'llm.retrying': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          stream.retrying = {
            attempt: Number(p.attempt ?? 0),
            max: Number(p.max ?? 0),
            reason: String(p.reason ?? ''),
          };
          // G2：清空本 used 双草稿（防“半截+完整”双拼）
          const tid = stream.currentTurn?.turnId;
          const used = stream.currentTurn?.activeOrd ?? 0;
          if (tid && used >= 1) messages.clearDraft(tid, used);
          break;
        }
        case 'usage': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          const session = useSessionStore();
          stream.usage.lastPromptTokens = Number(p.prompt ?? 0);
          stream.usage.lastCompletionTokens = Number(p.completion ?? 0);
          stream.usage.dropped = p.dropped === true;
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
          const turns = (p.turns ?? p.messages ?? []) as unknown;
          messages.applySnapshot({ seq, turns: turns as never });
          messages.setLastSeq(seq);
          break;
        }
        case 'session.contextCleared': {
          messages.clear();
          messages.setLastSeq(seq);
          clearCollapsedTools();
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
           const removed = typeof p.removed === 'number' ? (p.removed as number) : 0;
           session.compactionSummary = String(p.summary ?? '');
           session.compactionRemoved = removed;
           session.compactionKept = typeof p.totalAfter === 'number' ? Math.max(0, (p.totalAfter as number) - 2) : null;
           stream.compactionStarted = false;
           const seqNum = typeof p.seq === 'number' ? (p.seq as number) : 1;
           stream.notice = removed === 0
             ? { level: 'info', text: '消息太少，无需压缩' }
             : { level: 'info', text: `已完成第 ${seqNum} 次压缩，历史记录已就地折叠` };
           const turns = (p.turns ?? null) as unknown;
           if (Array.isArray(turns) && turns.length > 0) {
             messages.applySnapshot({ seq, turns: turns as never });
           } else if (Array.isArray(p.kept)) {
             // 老引擎兼容：kept 只是尾部，拉全量
             void messages.loadSnapshot();
           }
           messages.setLastSeq(seq);
           break;
         }
        case 'user.injected': {
          const p = asRecord(f.p);
          const stream = useStreamStore();
          const tid = stream.currentTurn?.turnId;
          if (tid) messages.appendInjected(tid, String(p.text ?? ''));
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
                turns?: Array<Record<string, unknown>>;
                messages?: Array<Record<string, unknown>>;
                agent?: Record<string, unknown>;
                session?: Record<string, unknown>;
                busy?: boolean;
                queue?: unknown[];
              }>('agent.resync', null);
              const cur = messages.lastSeq;
              const snapSeq = typeof snap.seq === 'number' ? snap.seq : seq;
              messages.applySnapshot({
                seq: Math.max(cur, seq, snapSeq),
                turns: (snap.turns ?? snap.messages ?? []) as never,
              });
              try {
                const ag = snap?.agent;
                if (ag && typeof ag === 'object') useSessionStore().applySnapshotAgent(ag);
                const ss = snap?.session as { id?: unknown; cwd?: unknown; historyPath?: unknown } | undefined;
                if (ss && typeof ss === 'object') {
                  useSessionStore().applyInfo({
                    sessionId: typeof ss.id === 'string' ? ss.id : undefined,
                    cwd: ss.cwd as string | undefined,
                    historyPath: (ss.historyPath ?? null) as string | null,
                  });
                }
              } catch {
                /* 会话元信息同步失败不影响重载 */
              }
              clearCollapsedTools();
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

export function __testOnlyNormalize(p: unknown): Record<string, unknown> {
  return asRecord(p);
}
export { normalizeToolCalls };
