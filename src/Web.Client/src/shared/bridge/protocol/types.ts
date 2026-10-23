/** need21-F02: 桥协议类型 barrel（旧 src/bridge/types.ts 全量迁移，BRIDGE_VERSION 单源见 version.ts）。 */
export * from './base';
export * from './providers';
export * from './llm';
export * from './domain';
export * from './hostApi';
export * from './errors';
export { BRIDGE_VERSION, assertBridgeVersion } from './version';
