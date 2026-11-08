import type { ContractEventFrame } from '@/contract/agent';
import type { AgentToolCard } from './model';

/**
 * Output volume ladder shared with the history lane: preview text on the
 * card, full text on disk, overflow spilled to a sidecar file. Counts stay
 * in one place so every lane truncates at the same line.
 */
export const TOOL_OUTPUT = {
  /** Card preview characters; overflow hides behind “view full text”. */
  display: 2048,
  /** History payload characters kept inline before spilling. */
  persist: 8192,
  /** Backend spill threshold in bytes; above it `spilledFile` carries the rest. */
  spill: 65536,
  /** Argument preview characters shown on the card header. */
  preview: 4096,
} as const;

/**
 * Acceptance matrix: every catalog tool must render a full card (params,
 * result, duration, full text, images, warnings) on the frame lane.
 */
export const TOOL_MATRIX: readonly string[] = [
  'read_file',
  'list_dir',
  'grep_search',
  'view_image',
  'exec_command',
  'edit_file',
  'edit_lines',
  'edit',
  'write_file',
  'undo_edit',
  'ask_user',
  'skill_tool',
  'memory_tool',
  'spawn_subagent',
  'goal_tool',
];

export type FrameCardStatus = 'running' | 'done' | 'error';

export interface FrameToolImage {
  imageId: string;
  fileName: string;
  mime: string;
  relPath: string;
  width: number;
  height: number;
  sizeBytes: number;
}

export interface FrameToolCard {
  callId: string;
  turnId: string;
  toolName: string;
  argsText: string;
  status: FrameCardStatus;
  output: string;
  fullOutput: string;
  outputTruncated: boolean;
  exitCode: number | null;
  durationMs: number | null;
  receivedBytes: number;
  fileBytes: number | null;
  errorCode: string | null;
  errorPayload: string;
  spilledFile: string;
  images: FrameToolImage[];
  validatedWarnings: string[];
  policy: string;
  updatedSeq: number;
}

export interface ToolFeedStats {
  applied: number;
  dedupDrops: number;
  rejectedLegacyArgs: number;
  coalescedDeltas: number;
}

function readString(record: Record<string, unknown>, key: string): string {
  const value = record[key];
  return typeof value === 'string' ? value : '';
}

function readNumberOrNull(record: Record<string, unknown>, key: string): number | null {
  const value = record[key];
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function readImages(record: Record<string, unknown>): FrameToolImage[] {
  const raw = record['images'];
  if (!Array.isArray(raw)) return [];
  const out: FrameToolImage[] = [];
  for (const entry of raw) {
    if (!entry || typeof entry !== 'object') continue;
    const item = entry as Record<string, unknown>;
    out.push({
      imageId: readString(item, 'imageId'),
      fileName: readString(item, 'fileName'),
      mime: readString(item, 'mime') || 'image/png',
      relPath: readString(item, 'relPath'),
      width: typeof item['width'] === 'number' ? (item['width'] as number) : 0,
      height: typeof item['height'] === 'number' ? (item['height'] as number) : 0,
      sizeBytes: typeof item['sizeBytes'] === 'number' ? (item['sizeBytes'] as number) : 0,
    });
  }
  return out;
}

function readWarnings(record: Record<string, unknown>): string[] {
  const raw = record['validatedWarnings'];
  if (!Array.isArray(raw)) return [];
  return raw.filter((entry): entry is string => typeof entry === 'string' && entry !== '');
}

function utf8Length(text: string): number {
  try {
    return new TextEncoder().encode(text).length;
  } catch {
    return text.length;
  }
}

/**
 * Render copy for machine-readable tool error codes. Only the headline is
 * mapped here; the raw output stays on the card for inspection.
 */
export function describeToolError(errorCode: string): string {
  switch (errorCode) {
    case 'OLD_STRING_NOT_FOUND':
      return '未找到待替换的原文，请检查文件是否已被修改';
    case 'REGEX_ZERO_LENGTH':
      return '正则匹配为空，请收紧匹配模式';
    case 'ANCHOR_TOO_SHORT':
      return '匹配锚点过短，请提供更长的上下文';
    case 'ANCHOR_AMBIGUOUS':
      return '匹配到多处相同文本，请扩大上下文消除歧义';
    case 'EXPECTED_HASH_MISMATCH':
      return '文件已被外部修改，校验未通过';
    case 'CONTENT_TOO_LARGE':
      return '写入内容超限，已拒绝落盘';
    case 'NEED_CONFIRM_OVERWRITE':
      return '目标文件已存在，需要确认后覆盖';
    case 'IMAGE_TOO_LARGE':
      return '图片过大，无法读取';
    case 'NOT_AN_IMAGE':
      return '所选文件不是有效图片';
    case 'IMAGE_MISSING':
      return '图片不存在或已被删除';
    case 'PATH_TRAVERSAL':
      return '路径越界，已拒绝访问';
    case 'BACKUP_INDEX_MISS':
      return '找不到对应的编辑备份，无法回滚';
    case 'COMMAND_TOO_LONG':
      return '命令超长，已拒绝执行';
    case 'INVALID_TIMEOUT':
      return '超时参数非法，请输入 1..300000 毫秒';
    case 'MISSING_LIMIT':
      return '缺少行数参数，无法定位编辑行';
    default:
      if (errorCode.indexOf('[SUBAGENT_') === 0) return '子代理执行受限，详见输出';
      if (errorCode.indexOf('[PERMISSION_DENIED') === 0) return '权限不足，已拒绝执行';
      if (errorCode.indexOf('[SKILL_VALIDATION]') === 0) return '技能参数校验未通过';
      if (errorCode.indexOf('COMMAND_') === 0) return '命令执行失败，详见输出';
      if (errorCode.indexOf('VALIDATED_') === 0) return '参数已被修正后执行，详见提示';
      return errorCode === '' ? '' : `工具执行失败（${errorCode}）`;
  }
}

const TOOL_FACES = new Set(['tool.start', 'tool.delta', 'tool.end']);

function hasLegacyArgsKey(params: Record<string, unknown>): boolean {
  return Object.prototype.hasOwnProperty.call(params, 'argsDelta') || Object.prototype.hasOwnProperty.call(params, 'argsJson');
}

function blankCard(turnId: string, callId: string, toolName: string, seq: number): FrameToolCard {
  return {
    callId,
    turnId,
    toolName,
    argsText: '',
    status: 'running',
    output: '',
    fullOutput: '',
    outputTruncated: false,
    exitCode: null,
    durationMs: null,
    receivedBytes: 0,
    fileBytes: null,
    errorCode: null,
    errorPayload: '',
    spilledFile: '',
    images: [],
    validatedWarnings: [],
    policy: '',
    updatedSeq: seq,
  };
}

/**
 * Powered adapter between the frame pump and the tool cards. `tool.start`
 * opens the card, `tool.delta` streams progress through the single `delta`
 * field with per-frame coalescing, and `tool.end` settles every field the
 * card matrix checks. Cards are plain data so the existing card components
 * render them unchanged.
 */
export class FrameToolFeed {
  private readonly cards = new Map<string, FrameToolCard>();
  private waters = 0;
  private applied = 0;
  private dedupDrops = 0;
  private rejectedLegacyArgs = 0;
  private coalescedDeltas = 0;

  get lastSeq(): number {
    return this.waters;
  }

  stats(): ToolFeedStats & { waters: number; lastSeq: number } {
    return {
      applied: this.applied,
      dedupDrops: this.dedupDrops,
      rejectedLegacyArgs: this.rejectedLegacyArgs,
      coalescedDeltas: this.coalescedDeltas,
      waters: this.waters,
      lastSeq: this.waters,
    };
  }

  reset(): void {
    this.cards.clear();
    this.waters = 0;
    this.applied = 0;
    this.dedupDrops = 0;
    this.rejectedLegacyArgs = 0;
    this.coalescedDeltas = 0;
  }

  cardOf(callId: string): FrameToolCard | undefined {
    return this.cards.get(callId);
  }

  cardsOfTurn(turnId: string): FrameToolCard[] {
    const out: FrameToolCard[] = [];
    for (const card of this.cards.values()) {
      if (card.turnId === turnId) out.push({ ...card, images: [...card.images], validatedWarnings: [...card.validatedWarnings] });
    }
    return out;
  }

  observe(frame: ContractEventFrame): boolean {
    if (!frame || frame.t !== 'evt') return false;
    if (typeof frame.seq !== 'number' || !TOOL_FACES.has(frame.k)) return false;
    if (frame.seq <= this.waters) {
      this.dedupDrops += 1;
      return false;
    }
    const params = (frame.p ?? {}) as Record<string, unknown>;
    if ((frame.k === 'tool.delta' || frame.k === 'tool.start') && hasLegacyArgsKey(params)) {
      this.waters = frame.seq;
      this.rejectedLegacyArgs += 1;
      return false;
    }
    switch (frame.k) {
      case 'tool.start':
        this.openCard(params, frame.seq);
        break;
      case 'tool.delta':
        this.streamDelta(params, frame.seq);
        break;
      case 'tool.end':
        this.closeCard(params, frame.seq);
        break;
      default:
        return false;
    }
    this.waters = frame.seq;
    this.applied += 1;
    return true;
  }

  private openCard(params: Record<string, unknown>, seq: number): void {
    const turnId = readString(params, 'turnId');
    const callId = readString(params, 'toolCallId') || readString(params, 'callId');
    if (turnId === '' || callId === '') return;
    let card = this.cards.get(callId);
    if (!card) {
      card = blankCard(turnId, callId, readString(params, 'toolName') || 'tool', seq);
      this.cards.set(callId, card);
    }
    const preview = readString(params, 'argsPreview');
    if (preview !== '') card.argsText = preview.slice(0, TOOL_OUTPUT.preview);
    const policy = readString(params, 'policy');
    if (policy !== '') card.policy = policy;
    card.updatedSeq = seq;
  }

  private streamDelta(params: Record<string, unknown>, seq: number): void {
    const turnId = readString(params, 'turnId');
    const callId = readString(params, 'toolCallId') || readString(params, 'callId');
    if (turnId === '' || callId === '') return;
    let card = this.cards.get(callId);
    if (!card) {
      card = blankCard(turnId, callId, readString(params, 'toolName') || 'tool', seq);
      this.cards.set(callId, card);
    }
    const delta = readString(params, 'delta');
    if (delta !== '') {
      card.argsText += delta;
      card.receivedBytes += utf8Length(delta);
    }
    card.updatedSeq = seq;
    this.coalescedDeltas += 1;
  }

  private closeCard(params: Record<string, unknown>, seq: number): void {
    const turnId = readString(params, 'turnId');
    if (turnId === '') return;
    const callId = readString(params, 'toolCallId') || readString(params, 'callId');
    let card = callId !== '' ? this.cards.get(callId) : undefined;
    if (!card) {
      const last = [...this.cards.values()].reverse().find((entry) => entry.turnId === turnId && entry.status === 'running');
      card = last;
    }
    if (!card) return;
    const okValue = params['ok'];
    const successValue = params['success'];
    const ok = typeof okValue === 'boolean' ? okValue : typeof successValue === 'boolean' ? successValue : true;
    card.status = ok ? 'done' : 'error';
    const output = readString(params, 'output') || readString(params, 'resultText');
    if (output !== '') {
      card.fullOutput = output;
      card.outputTruncated = output.length > TOOL_OUTPUT.display;
      card.output = card.outputTruncated ? `${output.slice(0, TOOL_OUTPUT.display)}\n...（输出过长已截断）` : output;
    }
    const exitCode = readNumberOrNull(params, 'exitCode');
    if (exitCode !== null) card.exitCode = exitCode;
    const durationMs = readNumberOrNull(params, 'durationMs');
    if (durationMs !== null) card.durationMs = durationMs;
    const fileBytes = readNumberOrNull(params, 'fileBytes');
    if (fileBytes !== null) card.fileBytes = fileBytes;
    const errorCode = readString(params, 'errorCode');
    if (errorCode !== '') card.errorCode = errorCode;
    const errorPayload = readString(params, 'errorPayload');
    if (errorPayload !== '') card.errorPayload = errorPayload;
    const spilledFile = readString(params, 'spilledFile');
    if (spilledFile !== '') card.spilledFile = spilledFile;
    const images = readImages(params);
    if (images.length > 0) card.images = images;
    const warnings = readWarnings(params);
    if (warnings.length > 0) card.validatedWarnings = warnings;
    card.updatedSeq = seq;
  }

  /**
   * Project a frame card onto the shape the card components already render,
   * so no component fork is needed for the frame lane.
   */
  toAgentCard(callId: string): AgentToolCard | undefined {
    const card = this.cards.get(callId);
    if (!card) return undefined;
    return {
      callId: card.callId,
      functionName: card.toolName,
      argsJson: card.argsText,
      status: card.status,
      resultText: card.output,
      exitCode: card.exitCode,
      durationMs: card.durationMs,
      iteration: 0,
      receivedBytes: card.receivedBytes,
      fileBytes: card.fileBytes,
      images: card.images.map((image) => ({
        imageId: image.imageId,
        fileName: image.fileName,
        mime: image.mime,
        relPath: image.relPath,
        width: image.width,
        height: image.height,
        sizeBytes: image.sizeBytes,
        sha16: image.imageId,
      })),
      errorCode: card.errorCode,
    };
  }
}
