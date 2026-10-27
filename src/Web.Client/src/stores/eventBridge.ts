// src/Web.Client/src/stores/eventBridge.ts
// D4-8 事件 → store 的唯一映射点（A-8.4：全应用唯一的帧处理点）。
import { useMessagesStore } from './messages';
import { useStreamStore } from './stream';
import { usePermissionsStore } from './permissions';
import { useSessionStore } from './session';
import { useUiStore } from './ui';
import type { HaloFrame } from '@/bridge/types';

/**
 * 事件 → store 的唯一映射点。
 *
 * ============ 为什么集中在一个函数 ============
 *  如果把事件处理分散到各组件，会出现：
 *    ① 组件挂载/卸载时处理器丢失 → 事件丢
 *    ② 同一条事件被多个组件重复处理 → 状态错乱
 *    ③ 排查"为什么状态没更新"要翻十几个文件
 *  集中以后：**一条事件只处理一次，一处在改状态**。
 * ============ 未识别的事件怎么办 ============
 *  记 debug 日志（不抛错、不忽略）。这样新增事件时能立刻发现前端没处理。
 * ============ ============
 */
export function dispatchFrames(frames: HaloFrame[]): void {
  if (frames.length === 0) return;

  const messages = useMessagesStore();
  const stream = useStreamStore();
  const permissions = usePermissionsStore();
  const session = useSessionStore();
  const ui = useUiStore();

  for (const f of frames) {
    // 去重（防止重连后重放）：lastSeq 只增不减
    if (f.seq <= messages.lastSeq) continue;

    switch (f.k) {
      // ---------- 回合生命周期 ----------
      case 'turn.started': {
        const p = f.p as { turnId?: string };
        const turnId = p.turnId ?? `turn-${f.seq}`;
        const msgId = messages.append({
          role: 'assistant',
          content: '',
          streaming: true,
        });
        stream.beginTurn(turnId, msgId);
        break;
      }

      case 'text.delta': {
        const p = f.p as { text?: string };
        const id = stream.streamingMessageId;
        if (id && p.text) messages.appendDelta(id, p.text);
        break;
      }

      case 'reasoning.delta': {
        const p = f.p as { text?: string };
        const id = stream.streamingMessageId;
        if (id && p.text) messages.appendReasoning(id, p.text);
        break;
      }

      case 'turn.finished': {
        const p = f.p as {
          turnId?: string;
          text?: string;
          truncated?: boolean;
          byUser?: boolean;
          byLength?: boolean;
          reason?: string;
        };
        const id = stream.streamingMessageId;
        if (id) {
          messages.patch(id, {
            streaming: false,
            truncatedByUser: p.byUser === true,
            truncatedByLength: p.byLength === true,
          });
        }
        stream.endTurn();
        break;
      }

      // ---------- 工具 ----------
      case 'tool.started': {
        const p = f.p as {
          toolCallId?: string;
          name?: string;
          arguments?: string;
        };
        const msgId = stream.streamingMessageId;
        if (p.toolCallId && msgId) {
          stream.addTool({
            toolCallId: p.toolCallId,
            messageId: msgId,
            name: p.name ?? '',
            arguments: p.arguments ?? '{}',
            startedAt: Date.now(),
            output: '',
            alertLevel: 0,
          });
          // 同时记到消息里（用于历史渲染）
          messages.patchToolCall(msgId, p.toolCallId, {
            status: 'running',
          });
        }
        break;
      }

      case 'tool.output': {
        const p = f.p as { toolCallId?: string; delta?: string };
        if (p.toolCallId) {
          const t = stream.activeTools.get(p.toolCallId);
          if (t) {
            t.output += p.delta ?? '';
            messages.patchToolCall(t.messageId, p.toolCallId, { output: t.output });
          }
        }
        break;
      }

      case 'silent.alert': {
        const p = f.p as { toolCallId?: string; level?: number };
        if (p.toolCallId) {
          stream.patchTool(p.toolCallId, { alertLevel: p.level ?? 0 });
          const t = stream.activeTools.get(p.toolCallId);
          if (t) {
            messages.patchToolCall(t.messageId, p.toolCallId, {
              alertLevel: p.level ?? 0,
            });
          }
        }
        break;
      }

      case 'tool.finished': {
        const p = f.p as {
          toolCallId?: string;
          result?: string;
          ok?: boolean;
          cancelled?: boolean;
        };
        if (p.toolCallId) {
          const t = stream.activeTools.get(p.toolCallId);
          const msgId = t?.messageId ?? stream.streamingMessageId;
          if (msgId) {
            messages.patchToolCall(msgId, p.toolCallId, {
              status: p.cancelled ? 'cancelled' : p.ok === false ? 'error' : 'done',
              result: p.result,
              ok: p.ok,
            });
          }
          stream.removeTool(p.toolCallId);
        }
        break;
      }

      // ---------- 权限询问 ----------
      case 'pending.ask': {
        const p = f.p as {
          askId?: string;
          kind?: string;
          title?: string;
          body?: string;
          warning?: string | null;
          allowAlways?: boolean;
        };
        if (p.askId) {
          permissions.setPending({
            askId: p.askId,
            kind: p.kind ?? 'unknown',
            title: p.title ?? '',
            body: p.body ?? '',
            warning: p.warning ?? null,
            allowAlways: p.allowAlways === true,
          });
        }
        break;
      }

      case 'pending.clear': {
        permissions.clearPending();
        break;
      }

      // ---------- 状态类 ----------
      case 'agent.busy': {
        const p = f.p as { busy?: boolean };
        if (p.busy === true) stream.busy = true;
        else if (p.busy === false) stream.endTurn();
        break;
      }

      case 'session.info': {
        const p = f.p as Record<string, unknown>;
        session.applyInfo(p);
        break;
      }

      case 'usage': {
        const p = f.p as { input?: number; output?: number };
        session.addUsage(p.input ?? 0, p.output ?? 0);
        break;
      }

      case 'notice': {
        // 交给 UI store（toast）：ui.ts 不反向依赖本文件，无循环依赖，可直接静态引用
        const p = f.p as { text?: string; level?: string };
        const text = p.text ?? '';
        if (p.level === 'error') ui.toast.error(text);
        else if (p.level === 'warn') ui.toast.warning(text);
        else ui.toast.info(text);
        break;
      }

      default: {
        // 未识别的事件：记日志（不抛错）
        console.debug(`[events] 未处理的事件类型：${f.k}`);
      }
    }

    messages.lastSeq = f.seq;
  }
}
