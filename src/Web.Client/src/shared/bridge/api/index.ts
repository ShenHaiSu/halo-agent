/** need21-F02: 域 API 组装（transport → 完整 HostBridgeApi；旧 wrapNativeHostBridge 的新形态）。 */
import type { BridgeTransport } from '../transport/transport';
import type { HostBridgeApi } from '../protocol/types';
import { createAgentApi } from './agent';
import { createConfigApi } from './config';
import { createSessionApi } from './session';
import { createSystemApi } from './system';

export { createAgentApi, startAgentTurn, type StartAgentTurnParams } from './agent';
export { createConfigApi } from './config';
export { createSessionApi } from './session';
export { createSystemApi } from './system';

/** 由传输层组装完整桥接 API（Cef 与 Mock 共用；归一/兼容内聚在各域内）。 */
export function createHostBridgeApi(t: BridgeTransport): HostBridgeApi {
  return {
    ...createSystemApi(t),
    ...createConfigApi(t),
    ...createAgentApi(t),
    ...createSessionApi(t),
  };
}
