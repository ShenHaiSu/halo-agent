import { CONTRACT_VERSION } from './agent';
import type {
  ContractErrorCode,
  ContractEventFrame,
  ContractEventKind,
  ContractFrame,
  ContractMethod,
  ContractRequestFrame,
  ContractResponseFrame,
} from './agent';
// Compile-time wiring: keeps the generated surface inside typecheck so a
// stale generation breaks the frontend build instead of failing silently.
export const contractVersionInUse: typeof CONTRACT_VERSION = CONTRACT_VERSION;

export function buildPromptRequest(id: number, text: string): ContractRequestFrame {
  const method: ContractMethod = 'agent.prompt';
  return { t: 'req', id, m: method, p: { text } };
}

export function describeFrame(frame: ContractFrame): string {
  switch (frame.t) {
    case 'req':
      return `req#${frame.id}:${frame.m}`;
    case 'res':
      return `res#${frame.id}:${frame.ok ? 'ok' : 'err'}`;
    case 'evt':
      return `evt#${frame.seq}:${frame.k}`;
  }
}

export function isTerminalEvent(kind: ContractEventKind): boolean {
  return kind === 'turn.done' || kind === 'turn.failed' || kind === 'turn.cancelled';
}

export function isRetryableError(code: ContractErrorCode): boolean {
  return code === 'RATE_LIMITED' || code === 'NETWORK_FAIL';
}

export function sampleResponse(id: number): ContractResponseFrame {
  return { t: 'res', id, ok: true, r: {} };
}

export function sampleEvent(seq: number): ContractEventFrame {
  return { t: 'evt', seq, k: 'usage.tick', p: {} };
}
