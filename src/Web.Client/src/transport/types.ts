import type { ContractErrorCode, ContractEventFrame, ContractMethod } from '../contract/agent';

/** Single method call result: success carries a payload, failure a typed error. */
export interface InvokeResult {
  ok: boolean;
  result?: Record<string, unknown>;
  error?: { code: ContractErrorCode; msg: string; hint?: string };
}

/** One batched read from the event journal. */
export interface EventBatch {
  frames: ContractEventFrame[];
  nextSeq: number;
  hasGap: boolean;
  droppedEstimate: number;
}

/**
 * Frame-level access to the backend. Business code talks only to this
 * interface; only the transport folder may touch the raw host object.
 */
export interface FrameTransport {
  invoke(method: ContractMethod, params?: Record<string, unknown>): Promise<InvokeResult>;
  pollBatch(sinceSeq: number, max: number): Promise<EventBatch>;
}

/** Largest single batch the host boundary serves. */
export const MAX_BATCH = 500;

/** Storage key for the persisted read cursor. */
export const SEQ_STORAGE_KEY = 'halo-frame-seq';
