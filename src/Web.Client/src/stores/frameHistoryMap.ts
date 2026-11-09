import type { ChatImageRef, UnifiedChatMessage } from '@shared/bridge/hostBridge';

/**
 * Mapping between frame history rows and the legacy render shape.
 * The engine ledger owns durable truth; this layer only translates one
 * history page into messages the existing timeline pipeline can consume.
 * Rows that cannot be read are skipped and counted, never thrown.
 */

/** Preview budget mirrored from the engine ledger: longer rows arrive cut. */
export const HISTORY_PREVIEW_LIMIT = 2048;

/** Raw history row as served by session.history under either envelope key. */
export type FrameHistoryRow = Record<string, unknown>;

/** Render-ready row: legacy message plus read-back affordances. */
export interface FrameMappedMessage extends UnifiedChatMessage {
  /** True when content is a preview and the remainder needs a follow-up read. */
  truncated: boolean;
  /** Durable backend message id, kept for reconciliation and debugging. */
  backendId: string;
  /** Relative spilled file path when the backend supplies one, else null. */
  spilledFile: string | null;
}

export interface MappedHistoryPage {
  messages: FrameMappedMessage[];
  skipped: number;
}

export type FullTextKind = 'inline' | 'preview-only' | 'open-path';

export interface FullTextTarget {
  kind: FullTextKind;
  path: string | null;
}

const KNOWN_ROLES = new Set(['user', 'assistant', 'tool', 'system']);

function firstString(row: FrameHistoryRow, keys: string[]): string | null {
  for (const key of keys) {
    const value = row[key];
    if (typeof value === 'string' && value !== '') return value;
  }
  return null;
}

function optionalString(row: FrameHistoryRow, keys: string[]): string | null {
  for (const key of keys) {
    const value = row[key];
    if (value === undefined || value === null) continue;
    const text = String(value);
    if (text !== '') return text;
  }
  return null;
}

function finiteNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function tokenOf(row: FrameHistoryRow, keys: string[]): number | null {
  for (const key of keys) {
    const hit = finiteNumber(row[key]);
    if (hit !== null) return hit;
  }
  return null;
}

function flagOf(row: FrameHistoryRow, keys: string[]): boolean {
  return keys.some((key) => row[key] === true);
}

function objectEntries(value: unknown): FrameHistoryRow[] {
  if (!Array.isArray(value)) return [];
  return value.filter((entry): entry is FrameHistoryRow => !!entry && typeof entry === 'object' && !Array.isArray(entry));
}

function toolNameOf(entry: FrameHistoryRow): string {
  const nested = entry['function'];
  if (nested && typeof nested === 'object' && !Array.isArray(nested)) {
    const name = (nested as FrameHistoryRow)['name'] ?? (nested as FrameHistoryRow)['Name'];
    if (typeof name === 'string' && name !== '') return name;
  }
  return firstString(entry, ['name', 'Name', 'functionName', 'FunctionName']) ?? 'tool';
}

function toolArgsOf(entry: FrameHistoryRow): string {
  const nested = entry['function'];
  if (nested && typeof nested === 'object' && !Array.isArray(nested)) {
    const args = (nested as FrameHistoryRow)['arguments'] ?? (nested as FrameHistoryRow)['Arguments'];
    if (typeof args === 'string') return args;
    if (args !== undefined && args !== null) return String(args);
  }
  const flat = entry['arguments'] ?? entry['Arguments'] ?? entry['argsText'] ?? entry['ArgsText'] ?? entry['argsJson'] ?? entry['ArgsJson'];
  if (typeof flat === 'string') return flat;
  if (flat !== undefined && flat !== null) return String(flat);
  return '';
}

function mapToolCalls(raw: unknown): FrameMappedMessage['toolCalls'] {
  const entries = objectEntries(raw);
  if (entries.length === 0) return null;
  const mapped = entries.map((entry) => ({
    id: firstString(entry, ['id', 'Id', 'callId', 'CallId', 'toolCallId', 'ToolCallId']) ?? '',
    type: 'function',
    function: {
      name: toolNameOf(entry),
      arguments: toolArgsOf(entry),
    },
  }));
  const usable = mapped.filter((call) => call.id !== '' || call.function.name !== '');
  return usable.length > 0 ? usable : null;
}

function mapImages(raw: unknown): ChatImageRef[] | null {
  const entries = objectEntries(raw);
  if (entries.length === 0) return null;
  return entries.map((entry) => ({
    imageId: firstString(entry, ['imageId', 'ImageId', 'id', 'Id']) ?? '',
    fileName: firstString(entry, ['fileName', 'FileName', 'name', 'Name']) ?? '',
    mime: firstString(entry, ['mime', 'Mime', 'mimeType', 'MimeType']) ?? '',
    relPath: firstString(entry, ['relPath', 'RelPath', 'path', 'Path']) ?? '',
    width: finiteNumber(entry['width'] ?? entry['Width']) ?? 0,
    height: finiteNumber(entry['height'] ?? entry['Height']) ?? 0,
    sizeBytes: finiteNumber(entry['sizeBytes'] ?? entry['SizeBytes'] ?? entry['size']) ?? 0,
    sha16: firstString(entry, ['sha16', 'Sha16', 'digest', 'Digest']) ?? '',
    lost: flagOf(entry, ['lost', 'Lost', 'isMissing', 'IsMissing', 'missing', 'Missing']),
  }));
}

/**
 * Translate one raw row. Returns null for unreadable rows so callers can
 * skip them without failing the whole page.
 */
export function mapHistoryRow(row: unknown): FrameMappedMessage | null {
  if (!row || typeof row !== 'object' || Array.isArray(row)) return null;
  const record = row as FrameHistoryRow;
  const index = finiteNumber(record['index'] ?? record['Index']);
  if (index === null) return null;
  const role = String(record['role'] ?? record['Role'] ?? '').toLowerCase();
  if (!KNOWN_ROLES.has(role)) return null;
  const turnId = firstString(record, ['turnId', 'TurnId']) ?? undefined;
  const content = optionalString(record, ['text', 'Text', 'content', 'Content']) ?? '';
  const truncated = flagOf(record, ['truncated', 'Truncated']) || content.length > HISTORY_PREVIEW_LIMIT;
  return {
    id: `msg_${index}_${turnId ?? 'na'}`,
    role: role as UnifiedChatMessage['role'],
    content,
    reasoningContent: optionalString(record, ['reasoningContent', 'ReasoningContent']),
    toolCalls: mapToolCalls(record['toolCalls'] ?? record['ToolCalls']),
    toolCallId: firstString(record, ['toolCallId', 'ToolCallId']),
    name: firstString(record, ['name', 'Name']),
    inputTokens: tokenOf(record, ['inputTokens', 'InputTokens']),
    outputTokens: tokenOf(record, ['outputTokens', 'OutputTokens']),
    cachedTokens: tokenOf(record, ['cachedTokens', 'CachedTokens']),
    createdAtUtc:
      firstString(record, ['createdAtUtc', 'CreatedAtUtc', 'createdAt', 'CreatedAt']) ??
      firstString(record, ['storedAt', 'StoredAt']) ??
      '',
    images: mapImages(record['images'] ?? record['Images']),
    truncated,
    backendId: firstString(record, ['id', 'Id', 'msgId', 'MsgId', 'messageId', 'MessageId']) ?? '',
    spilledFile: firstString(record, ['spilledFile', 'SpilledFile', 'spilledPath', 'SpilledPath']),
  };
}

/** Read one history page under either envelope key, counting skipped rows. */
export function mapHistoryPage(payload: unknown): MappedHistoryPage {
  let rows: unknown[] = [];
  if (Array.isArray(payload)) {
    rows = payload;
  } else if (payload && typeof payload === 'object' && !Array.isArray(payload)) {
    const record = payload as FrameHistoryRow;
    const nested = record['items'] ?? record['messages'] ?? record['Items'] ?? record['Messages'];
    if (Array.isArray(nested)) rows = nested;
  }
  const messages: FrameMappedMessage[] = [];
  let skipped = 0;
  for (const row of rows) {
    const mapped = mapHistoryRow(row);
    if (mapped) messages.push(mapped);
    else skipped += 1;
  }
  return { messages, skipped };
}

/** Backend page total when present, else the supplied fallback count. */
export function readPageTotal(payload: unknown, fallback: number): number {
  if (payload && typeof payload === 'object' && !Array.isArray(payload)) {
    const record = payload as FrameHistoryRow;
    for (const key of ['total', 'Total', 'messageCount', 'MessageCount']) {
      const hit = finiteNumber(record[key]);
      if (hit !== null) return Math.max(0, Math.floor(hit));
    }
  }
  return Math.max(0, Math.floor(fallback));
}

/**
 * Tool card count for parity checks: one card per distinct tool call id,
 * plus one per id-less tool row. Assistant declarations and tool result
 * rows that share an id collapse into a single card.
 */
export function countToolCards(messages: ReadonlyArray<UnifiedChatMessage>): number {
  const ids = new Set<string>();
  let bare = 0;
  for (const message of messages) {
    if (!message) continue;
    const calls = (message as UnifiedChatMessage).toolCalls;
    if (Array.isArray(calls)) {
      for (const call of calls) {
        const id = String((call as { id?: unknown })?.id ?? '');
        if (id !== '') ids.add(id);
        else bare += 1;
      }
    }
    if (String(message.role ?? '').toLowerCase() === 'tool') {
      const callId = String((message as unknown as { toolCallId?: unknown }).toolCallId ?? '');
      if (callId !== '') ids.add(callId);
      else bare += 1;
    }
  }
  return ids.size + bare;
}

/** Images flagged missing at read time; mirrors the backend missing marker. */
export function countMissingImages(messages: ReadonlyArray<UnifiedChatMessage>): number {
  let missing = 0;
  for (const message of messages) {
    const images = (message as UnifiedChatMessage).images;
    if (!Array.isArray(images)) continue;
    for (const image of images) {
      if ((image as ChatImageRef)?.lost === true) missing += 1;
    }
  }
  return missing;
}

/**
 * Full-text affordance for a mapped row: rows carrying a spilled file open
 * through the host path bridge, cut rows without one stay explicit previews,
 * plain rows render inline. No silent failure in any branch.
 */
export function resolveFullTextTarget(message: UnifiedChatMessage): FullTextTarget {
  const extra = message as Partial<FrameMappedMessage>;
  if (typeof extra.spilledFile === 'string' && extra.spilledFile !== '') {
    return { kind: 'open-path', path: extra.spilledFile };
  }
  if (extra.truncated === true) return { kind: 'preview-only', path: null };
  return { kind: 'inline', path: null };
}
