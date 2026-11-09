import type { ContractMethod } from '../contract/agent';
import type { FrameTransport, InvokeResult } from './types';
import { BridgeTransport } from './bridgeTransport';
import { MockTransport } from './mockTransport';

/** Whole-chain switch keys: the frame chain is the default, the legacy chain stays one explicit flip away. */
export const NEW_ENGINE_KEY = 'useNewEngine';
export const DISABLE_PUMP_KEY = 'disableEventPump';

/** Which backing the frame surface should use. */
export type TransportKind = 'mock' | 'bridge';

/** Read the persisted whole-chain switch (default on: only an explicit 'false' parks the legacy chain). */
export function isNewEngineEnabled(): boolean {
  try {
    return localStorage.getItem(NEW_ENGINE_KEY) !== 'false';
  } catch {
    return true;
  }
}

/** Persist the whole-chain switch; callers must reload or rebind after flipping. */
export function setNewEngineEnabled(on: boolean): void {
  try {
    localStorage.setItem(NEW_ENGINE_KEY, on ? 'true' : 'false');
  } catch {
    /* storage unavailable: keep running on the current chain */
  }
}

/** Whether the background event pump is paused (manual snapshot refresh only). */
export function isEventPumpDisabled(): boolean {
  try {
    return localStorage.getItem(DISABLE_PUMP_KEY) === 'true';
  } catch {
    return false;
  }
}

/**
 * Build the frame transport for the requested backing. Production uses the
 * host boundary; development and tests use the in-memory mock. There is no
 * per-method mixing: the caller picks one chain for the whole session.
 */
export function createTransport(kind: TransportKind): FrameTransport {
  return kind === 'mock' ? new MockTransport() : new BridgeTransport();
}

/**
 * Whole-chain adapter over the pre-existing direct-call bridge. It exposes
 * the frame surface but routes through one legacy entry point, so rollback
 * is a single switch flip without touching business code.
 */
export class LegacyTransport implements FrameTransport {
  private nextId = 1;

  constructor(private readonly legacyCall: (method: string, ...args: unknown[]) => Promise<unknown>) {}

  async invoke(method: ContractMethod, params: Record<string, unknown> = {}): Promise<InvokeResult> {
    const id = this.nextId++;
    try {
      const raw = await this.legacyCall(method, JSON.stringify({ t: 'req', id, m: method, p: params }));
      if (typeof raw === 'string') {
        const frame = JSON.parse(raw) as { ok?: boolean; r?: Record<string, unknown>; e?: InvokeResult['error'] };
        if (frame && typeof frame.ok === 'boolean') {
          return frame.ok
            ? { ok: true, result: frame.r ?? {} }
            : { ok: false, error: frame.e ?? { code: 'INTERNAL', msg: 'Legacy call failed.' } };
        }
      }
      return { ok: true, result: { legacy: true, raw: raw as unknown as Record<string, unknown> } };
    } catch (err) {
      return {
        ok: false,
        error: { code: 'NETWORK_FAIL', msg: err instanceof Error ? err.message : 'Legacy call failed.' },
      };
    }
  }

  async pollBatch(sinceSeq: number): Promise<{ frames: []; nextSeq: number; hasGap: false; droppedEstimate: 0 }> {
    return { frames: [], nextSeq: sinceSeq, hasGap: false as const, droppedEstimate: 0 };
  }
}
