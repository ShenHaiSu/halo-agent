import type { UnifiedChatMessage, ChatImageRef } from '@shared/bridge/hostBridge';

export interface TurnToolRef {
  id: string;
  name: string;
}
export interface TurnGroup {
  key: string;
  assistantId: string;
  reasoning: string | null;
  content: string | null;
  toolCalls: TurnToolRef[];
  toolMessages: UnifiedChatMessage[];
  telemetry: { inputTokens: number | null; cachedTokens: number | null; outputTokens: number | null } | null;
  createdAtUtc: string;
}

export function imagesOf(msg: UnifiedChatMessage | null | undefined): ChatImageRef[] {
  if (!msg) return [];
  const imgs = (msg as UnifiedChatMessage).images;
  return Array.isArray(imgs) ? imgs : [];
}

const IMAGE_PLACEHOLDER_RE = /\[image:([A-Za-z0-9_-]+)\]/g;

export function orderImagesByContent(content: string | null | undefined, images: ChatImageRef[] | null | undefined): ChatImageRef[] {
  const list = Array.isArray(images) ? images : [];
  if (list.length <= 1) return list.slice();
  const text = String(content ?? '');
  if (!text) return list.slice();

  const order = new Map<string, number>();
  IMAGE_PLACEHOLDER_RE.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = IMAGE_PLACEHOLDER_RE.exec(text)) !== null) {
    const id = m[1];
    if (!order.has(id)) order.set(id, order.size);
  }
  if (order.size === 0) return list.slice();

  return list
    .map((img, index) => {
      const id = String(img?.imageId ?? '').trim();
      const anchor = id && order.has(id) ? (order.get(id) as number) : Number.MAX_SAFE_INTEGER;
      return { img, index, anchor };
    })
    .sort((a, b) => (a.anchor !== b.anchor ? a.anchor - b.anchor : a.index - b.index))
    .map((x) => x.img);
}

export function orderedImagesOf(msg: UnifiedChatMessage | null | undefined): ChatImageRef[] {
  if (!msg) return [];
  return orderImagesByContent((msg as UnifiedChatMessage).content, imagesOf(msg));
}

export function mergeKeyOf(msg: UnifiedChatMessage): string {
  const base = String(msg.id || '');
  const imgs = imagesOf(msg);
  if (imgs.length === 0) return base;
  return `${base}#img:${imgs.map((i) => i.sha16 || i.imageId).join(',')}`;
}

export interface TimelineEntry {
  key: string;
  kind: 'user' | 'system' | 'turn' | 'user-turn';
  msg?: UnifiedChatMessage;
  /** @deprecated 主时间轴不再产生旧轮次条目，仅子会话目录兼容保留。 */
  group?: TurnGroup;
  /** @deprecated 合并舱已删除，仅类型兼容保留。 */
  run?: unknown;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  turn?: any;
}

export function blockHash(s: string): string {
  let h = 0x811c9dc5;
  const str = String(s || '');
  for (let i = 0; i < str.length; i++) {
    h ^= str.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return (h >>> 0).toString(16).padStart(8, '0') + ':' + str.length;
}

export function exportSessionMarkdown(messages: UnifiedChatMessage[]): string {
  const lines: string[] = [];
  for (const msg of messages) {
    const role = String(msg.role || '').toLowerCase();
    const head =
      role === 'user'
        ? '## 用户'
        : role === 'assistant'
          ? '## 助手'
          : role === 'tool'
            ? `## 工具${msg.name ? `（${msg.name}）` : ''}`
            : '## 系统';
    lines.push(head, '');
    if (msg.content) lines.push(String(msg.content), '');
    for (const img of imagesOf(msg)) {
      if ((img as ChatImageRef).lost) lines.push(`[图片已丢失：${img.fileName}]`, '');
      else lines.push(`![${img.fileName}](${img.relPath})`, '');
    }
  }
  return lines.join('\n');
}
