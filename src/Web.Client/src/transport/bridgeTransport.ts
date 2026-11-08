import type { ContractErrorCode, ContractEventFrame, ContractMethod } from '../contract/agent';
import type { EventBatch, FrameTransport, InvokeResult } from './types';
import { MAX_BATCH } from './types';

/**
 * Production frame transport over the thin host boundary.
 * Delegates to the host-owned `invoke` / `getEventBatch` pair exposed on
 * the raw host object; this file is the only production place allowed to
 * touch that object, so business code stays host-free.
 */
export class BridgeTransport implements FrameTransport {
  private requestId = 1;

  private rawHost(): Record<string, unknown> | null {
    try {
      const host = (globalThis as unknown as { hostBridge?: unknown }).hostBridge;
      if (!host || typeof host !== 'object') return null;
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
        hint: 'The desktop host has not exposed the frame channel yet.',
      },
    };
  }

  async invoke(method: ContractMethod, params: Record<string, unknown> = {}): Promise<InvokeResult> {
    const host = this.rawHost();
    const target = host?.['invoke'];
    if (typeof target !== 'function') return this.missingHostError(method);
    const payload = JSON.stringify({ t: 'req', id: this.requestId++, m: method, p: params });
    const raw = await (target as (body: string) => Promise<string>).call(host, payload);
    return parseInvokeResult(raw);
  }

  async pollBatch(sinceSeq: number, max: number): Promise<EventBatch> {
    const host = this.rawHost();
    const target = host?.['getEventBatch'];
    if (typeof target !== 'function') {
      return { frames: [], nextSeq: sinceSeq, hasGap: false, droppedEstimate: 0 };
    }
    const take = Math.min(Math.max(max, 1), MAX_BATCH);
    const raw = await (target as (since: number, count: number) => Promise<string>).call(host, sinceSeq, take);
    return parseEventBatch(raw, sinceSeq);
  }
}

interface RawResponse {
  t?: string;
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

export function parseInvokeResult(raw: string): InvokeResult {
  let frame: RawResponse;
  try {
    frame = JSON.parse(raw) as RawResponse;
  } catch {
    return { ok: false, error: { code: 'INTERNAL', msg: 'Malformed response frame.' } };
  }
  if (!frame || frame.t !== 'res' || typeof frame.ok !== 'boolean') {
    return { ok: false, error: { code: 'INTERNAL', msg: 'Malformed response frame.' } };
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
