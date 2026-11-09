import type { ContractErrorCode, ContractEventFrame, ContractMethod } from '../contract/agent';
/**
 * Canonical retry rule lives in contract/agentSmoke.ts (isRetryableError);
 * this transport mirrors that two-code set locally because agentSmoke has no
 * runtime entry in the bundler graph. A change here must update the canonical
 * copy first; no new codes invented.
 */
const RETRYABLE_CODES: readonly ContractErrorCode[] = ['RATE_LIMITED', 'NETWORK_FAIL'];

function isRetryableError(code: ContractErrorCode): boolean {
  return RETRYABLE_CODES.includes(code);
}
import type { EventBatch, FrameTransport, InvokeResult } from './types';
import { MAX_BATCH } from './types';
import { getNativeFrame } from '../shared/bridge/nativeFrame';

/**
 * Production frame transport over the thin host boundary.
 * Delegates to the host-owned `invoke` / `getEventBatch` pair exposed on
 * the frame object (`hostFrame`, falling back to `hostBridge` when the
 * shell still serves the legacy facade); this file is the only production
 * place allowed to touch those objects, so business code stays host-free.
 *
 * Layered timeouts: invoke replies wait up to INVOKE_TIMEOUT_MS, except the
 * prompt first packet which allows PROMPT_FIRST_PACKET_MS. The whole round
 * has no watchdog: once `ok` returns, the caller waits on events instead of
 * the invoke timer. Poll timeouts never throw; the pump counts them as a
 * failed step and backs off.
 */

export const INVOKE_TIMEOUT_MS = 15000;
export const PROMPT_FIRST_PACKET_MS = 30000;
export const POLL_TIMEOUT_MS = 15000;

const ENVELOPE_ID_SESSION_KEY = 'halo-frame-req-id';

function nextEnvelopeId(): number {
  try {
    const scope = globalThis as unknown as { sessionStorage?: Storage };
    const store = scope.sessionStorage;
    if (store) {
      const raw = store.getItem(ENVELOPE_ID_SESSION_KEY);
      const base = raw === null ? Math.floor(Math.random() * 100000) : Number(raw);
      const start = Number.isFinite(base) && base >= 0 ? Math.floor(base) : 0;
      const id = start + 1;
      try {
        store.setItem(ENVELOPE_ID_SESSION_KEY, String(id));
      } catch {
        /* keep the in-memory id when persistence is unavailable */
      }
      return id;
    }
  } catch {
    /* session storage unavailable: fall through to the process counter */
  }
  return (nextEnvelopeId.counter += 1);
}
nextEnvelopeId.counter = Math.floor(Math.random() * 100000);

function timeoutError(method: string): InvokeResult {
  return {
    ok: false,
    error: {
      code: 'NETWORK_FAIL',
      msg: `Timed out waiting for ${method}.`,
      hint: 'retry-after-backoff',
    },
  };
}

function withTimeout<T>(work: Promise<T>, ms: number): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | null = null;
  const guard = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(new Error('transport-timeout')), ms);
  });
  return Promise.race([work, guard]).finally(() => {
    if (timer) clearTimeout(timer);
  }) as Promise<T>;
}

export interface BridgeTimeouts {
  invokeMs?: number;
  promptMs?: number;
  pollMs?: number;
}

export class BridgeTransport implements FrameTransport {
  private readonly invokeMs: number;
  private readonly promptMs: number;
  private readonly pollMs: number;

  constructor(timeouts: BridgeTimeouts = {}) {
    this.invokeMs = timeouts.invokeMs ?? INVOKE_TIMEOUT_MS;
    this.promptMs = timeouts.promptMs ?? PROMPT_FIRST_PACKET_MS;
    this.pollMs = timeouts.pollMs ?? POLL_TIMEOUT_MS;
  }

  async invoke(method: ContractMethod, params: Record<string, unknown> = {}): Promise<InvokeResult> {
    const limit = method === 'agent.prompt' ? this.promptMs : this.invokeMs;
    const first = await this.invokeOnce(method, params, limit);
    if (first.ok) return first;
    const code = first.error?.code;
    if (code && isRetryableError(code)) {
      await sleep(200);
      return this.invokeOnce(method, params, limit);
    }
    return first;
  }

  private async invokeOnce(method: ContractMethod, params: Record<string, unknown>, limitMs: number): Promise<InvokeResult> {
    const host = this.rawHost();
    const target = host?.['invoke'];
    if (typeof target !== 'function') return this.missingHostError(method);
    const id = nextEnvelopeId();
    const payload = JSON.stringify({ t: 'req', id, m: method, p: params });
    let raw: string;
    try {
      raw = await withTimeout((target as (body: string) => Promise<string>).call(host, payload), limitMs);
    } catch {
      return timeoutError(method);
    }
    return parseInvokeResult(raw, id);
  }

  async pollBatch(sinceSeq: number, max: number): Promise<EventBatch> {
    const host = this.rawHost();
    const target = host?.['getEventBatch'];
    if (typeof target !== 'function') {
      return { frames: [], nextSeq: sinceSeq, hasGap: false, droppedEstimate: 0 };
    }
    const take = Math.min(Math.max(max, 1), MAX_BATCH);
    try {
      const raw = await withTimeout((target as (since: number, count: number) => Promise<string>).call(host, sinceSeq, take), this.pollMs);
      return parseEventBatch(raw, sinceSeq);
    } catch {
      // Poll timeouts are transport failures, not exceptions: the pump
      // records the miss and retries on its backoff schedule.
      return { frames: [], nextSeq: sinceSeq, hasGap: false, droppedEstimate: 0, timedOut: true } as EventBatch;
    }
  }

  private rawHost(): Record<string, unknown> | null {
    // Prefer the cached native frame so a wrapped legacy facade never masks it.
    try {
      const native = getNativeFrame();
      if (native) return native;
    } catch {
      /* fall through to the direct scope read */
    }
    try {
      const scope = globalThis as unknown as { hostFrame?: unknown; hostBridge?: unknown };
      const host = scope.hostFrame ?? scope.hostBridge;
      if (!host || typeof host !== 'object') return null;
      if ((host as Record<string, unknown>).__haloWrappedApi === true) return null;
      return host as Record<string, unknown>;
    } catch {
      return null;
    }
  }

  private missingHostError(method: string): InvokeResult {
    return {
      ok: false,
      error: {
        code: 'NETWORK_FAIL',
        msg: `Host boundary is unavailable for ${method}.`,
        hint: 'retry-after-attach',
      },
    };
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

interface RawResponse {
  t?: string;
  id?: number;
  ok?: boolean;
  r?: Record<string, unknown>;
  e?: { code?: string; msg?: string; hint?: string };
}

const KNOWN_ERRORS: ContractErrorCode[] = [
  'INVALID_PARAMS',
  'NOT_FOUND',
  'TURN_IN_PROGRESS',
  'COMPACTING',
  'RATE_LIMITED',
  'PROVIDER_AUTH',
  'NETWORK_FAIL',
  'TOOL_DENIED',
  'TOOL_TIMEOUT',
  'CANCELLED',
  'INTERNAL',
];

export function parseInvokeResult(raw: string, expectedId?: number): InvokeResult {
  let frame: RawResponse;
  try {
    frame = JSON.parse(raw) as RawResponse;
  } catch {
    return { ok: false, error: { code: 'INTERNAL', msg: 'Malformed response frame.' } };
  }
  if (!frame || frame.t !== 'res' || typeof frame.ok !== 'boolean') {
    return { ok: false, error: { code: 'INTERNAL', msg: 'Malformed response frame.' } };
  }
  if (typeof expectedId === 'number' && typeof frame.id === 'number' && frame.id !== expectedId) {
    return {
      ok: false,
      error: { code: 'INTERNAL', msg: `Mismatched response id (want ${expectedId}, got ${frame.id}).` },
    };
  }
  if (frame.ok) return { ok: true, result: frame.r ?? {} };
  const code = KNOWN_ERRORS.includes(frame.e?.code as ContractErrorCode) ? (frame.e?.code as ContractErrorCode) : 'INTERNAL';
  return {
    ok: false,
    error: { code, msg: frame.e?.msg ?? 'Request failed.', hint: frame.e?.hint },
  };
}

export function parseEventBatch(raw: string, sinceSeq: number): EventBatch {
  let doc: { frames?: ContractEventFrame[]; nextSeq?: number; hasGap?: boolean; droppedEstimate?: number };
  try {
    doc = JSON.parse(raw) as typeof doc;
  } catch {
    return { frames: [], nextSeq: sinceSeq, hasGap: false, droppedEstimate: 0 };
  }
  const frames = Array.isArray(doc.frames) ? doc.frames.filter((f) => f && f.t === 'evt') : [];
  return {
    frames,
    nextSeq: typeof doc.nextSeq === 'number' ? doc.nextSeq : sinceSeq,
    hasGap: doc.hasGap === true,
    droppedEstimate: typeof doc.droppedEstimate === 'number' ? doc.droppedEstimate : 0,
  };
}
