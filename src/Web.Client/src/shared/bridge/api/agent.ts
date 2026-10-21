/** need21-F02: 智能体域 API（startAgentTurn 对象参经 compat 唯一收敛做 7→6→5 回退；其余签名不变）。 */
import type { BridgeTransport } from '../transport/transport';
import type { BridgeResponse, HostBridgeApi, UnifiedLlmRequestDto } from '../protocol/types';
import { compatCallStartAgentTurn, type StartAgentTurnParams } from '../compat';
import { normalizeBridgePayload } from './normalize';

export type { StartAgentTurnParams };

/** 对象参 startAgentTurn（内部调 compat 做 7→6→5 回退，业务不再 try/catch）。 */
export async function startAgentTurn(t: BridgeTransport, params: StartAgentTurnParams): Promise<BridgeResponse<string>> {
  const res = await compatCallStartAgentTurn((...args: string[]) => t.call('startAgentTurn', ...args), params);
  return normalizeBridgePayload<BridgeResponse<string>>(res);
}

export function createAgentApi(
  t: BridgeTransport
): Pick<
  HostBridgeApi,
  'startAgentTurn' | 'cancelAgentTurn' | 'startSessionRun' | 'cancelSessionRun' | 'getImageThumbnail' | 'resolveAskUser'
> {
  return {
    // HostBridgeApi 位置参签名（内部转对象参走 compat 唯一收敛；调用方如 agentStore 无需改动）。
    async startAgentTurn(
      workspaceId: string,
      sessionId: string,
      userText: string,
      providerId?: string,
      modelId?: string,
      attachmentsJson?: string,
      requestId?: string
    ) {
      return startAgentTurn(t, {
        workspaceId,
        sessionId,
        userText,
        providerId,
        modelId,
        attachmentsJson,
        requestId,
      });
    },
    async startSessionRun(request: UnifiedLlmRequestDto | string) {
      const payload = typeof request === 'string' ? request : JSON.stringify(request);
      return normalizeBridgePayload<BridgeResponse<string>>(await t.call('startSessionRun', payload));
    },
    async cancelSessionRun(sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await t.call('cancelSessionRun', sessionId));
    },
    async cancelAgentTurn(sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await t.call('cancelAgentTurn', sessionId));
    },
    async getImageThumbnail(workspaceId: string, sessionId: string, imageId: string) {
      try {
        if (t.supports('getImageThumbnail')) {
          return normalizeBridgePayload<BridgeResponse<string>>(await t.call('getImageThumbnail', workspaceId, sessionId, imageId));
        }
      } catch (e) {
        console.warn('[bridge] native call failed, fallback empty state:', (e as Error)?.message ?? e);
      }
      return { ok: false, code: 'THUMB_UNAVAILABLE', message: '缩略图接口不可用' };
    },
    // need09-06：问询回答提交（老后端无此方法时报 ASK_UNSUPPORTED，不丢草稿）
    async resolveAskUser(sessionId: string, ticketId: string, answersJson: string) {
      try {
        if (t.supports('resolveAskUser')) {
          return normalizeBridgePayload<BridgeResponse<boolean>>(
            await t.call('resolveAskUser', sessionId ?? '', ticketId ?? '', answersJson ?? '')
          );
        }
      } catch (e) {
        console.warn('[bridge] native call failed, fallback empty state:', (e as Error)?.message ?? e);
      }
      return { ok: false, code: 'ASK_UNSUPPORTED', message: '问询提交通道不可用' };
    },
  };
}
