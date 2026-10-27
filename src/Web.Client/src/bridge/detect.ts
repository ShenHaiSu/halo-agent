// src/Web.Client/src/bridge/detect.ts
// D4-7 三态检测的规范 import 位置（实现在 bind.ts，避免循环依赖）。
export { hasHost, hasAgent, forceMock, isMock } from './bind';
