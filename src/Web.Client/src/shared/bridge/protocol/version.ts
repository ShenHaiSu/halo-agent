/**
 * need21-B05/F02: 桥协议数值版本单源（与后端 HaloAgent.Bridge.Contracts.BridgeVersion.ProtocolVersion 对齐）。
 * 历史值见 bridge/types.ts 的 BRIDGE_VERSION；本文件为领域化后的契约归属，B05 BridgeVersionTests 断言三处一致。
 */
import { reportBridgeError, toBridgeError } from './errors';

export const BRIDGE_VERSION = 2;

/** 版本断言（旧 client.ts negotiate 语义原样迁移）：一致通过；缺失按 legacy(0) 显式 warn 继续；不一致抛 BRIDGE_VERSION_MISMATCH。 */
export function assertBridgeVersion(backendVersion: number | null | undefined): number {
  if (backendVersion === null || backendVersion === undefined || Number.isNaN(backendVersion)) {
    // 旧后端无版本接口：显式 warn 标记 legacy（version 0），不断连、不静默吞掉
    console.warn(
      `[bridge] BRIDGE_VERSION_MISMATCH: 后端未上报桥接版本（期望 BRIDGE_VERSION=${BRIDGE_VERSION}），按 legacy(0) 继续，请升级后端`
    );
    return 0;
  }
  if (backendVersion === BRIDGE_VERSION) return backendVersion;
  const err = toBridgeError('BRIDGE_VERSION_MISMATCH', `桥接版本不一致：前端=${BRIDGE_VERSION}，后端=${String(backendVersion)}`);
  reportBridgeError(err);
  throw Object.assign(new Error(err.message), err);
}
