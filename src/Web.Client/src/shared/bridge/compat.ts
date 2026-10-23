/** need21-F02: startAgentTurn 7→6→5 参数回退唯一收敛点（旧 client.ts callStartAgentTurn + wrap 内回退合一，业务不再 try/catch）。 */
import { reportBridgeError, serializeWithDates, toBridgeError } from './protocol/errors';

export interface StartAgentTurnParams {
  workspaceId: string;
  sessionId: string;
  userText: string;
  providerId?: string;
  modelId?: string;
  attachmentsJson?: string;
  requestId?: string;
}

/** 原生 startAgentTurn 调用签名（7 参全量；老后端逐级回退由本函数消化）。 */
export type RawStartAgentTurn = (...args: string[]) => Promise<unknown>;

/** 7 参→6 参→5 参回退，每次回退 warn + code，不吞错；最终失败抛 {code,message}。 */
export async function compatCallStartAgentTurn(rawStartAgentTurn: RawStartAgentTurn, params: StartAgentTurnParams): Promise<unknown> {
  const p = serializeWithDates(params);
  const base5 = [p.workspaceId, p.sessionId, p.userText, p.providerId ?? '', p.modelId ?? ''];
  const extra = p.attachmentsJson ?? '';
  const reqId = p.requestId ?? '';
  // 7 参（含 requestId；新后端回显去重）
  try {
    return await rawStartAgentTurn(...base5, extra, reqId);
  } catch (e) {
    console.warn('[bridge] startAgentTurn 7-arg failed, fallback 6-arg:', (e as Error)?.message ?? e);
  }
  // 6 参（含 attachments）
  try {
    return await rawStartAgentTurn(...base5, extra);
  } catch (e) {
    console.warn('[bridge] startAgentTurn 6-arg failed, fallback 5-arg:', (e as Error)?.message ?? e);
  }
  // 5 参（最后手段；再失败直接抛 {code,message}）
  try {
    return await rawStartAgentTurn(...base5);
  } catch (e) {
    const err = toBridgeError('AGENT_START_ERROR', `startAgentTurn 失败：${e instanceof Error ? e.message : String(e)}`);
    reportBridgeError(err);
    throw Object.assign(new Error(err.message), err);
  }
}
