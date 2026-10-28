// src/Web.Client/src/bridge/types.ts
// D4-7 桥接类型声明：window.haloHost（12 方法）/ haloAgent（3 方法）/ 事件入口。
//
// 分层：bridge 只定义类型，不依赖任何业务模块。

 /** window.haloHost（宿主业务接口，12 个方法；第 12 个 windowDragStart 为自绘标题栏 B 方案显式拖拽入口）。
  *
  * 桥契约（dist 骨架屏根因修复）：凡返回 DTO 的方法（windowMaximizeOrRestore /
  * windowState / systemInfo），C# 侧返回 JSON 字符串（camelCase），调用方负责 parse。
  * 绝不能假设桥直接返回对象（CefSharp 对复杂返回值的编组不稳定且按 C# 原名返回）。
  */
 export interface HaloHost {
   windowMinimize(): Promise<void>;
  /** 显式启动窗口拖拽（B 方案）：前端标题栏 mousedown 时调用，最大化/最小化下宿主侧直接返回。 */
  windowDragStart(): Promise<void>;
   /** 返回 WindowState 的 JSON 字符串。 */
   windowMaximizeOrRestore(): Promise<string>;
   windowClose(): Promise<void>;
   /** 返回 WindowState 的 JSON 字符串。 */
   windowState(): Promise<string>;
   /** 返回 HostSystemInfo 的 JSON 字符串。 */
   systemInfo(): Promise<string>;
   openExternal(url: string): Promise<void>;
   showItemInFolder(path: string): Promise<void>;
   copyText(text: string): Promise<void>;
   readClipboardImage(): Promise<string | null>;
   toggleDevTools(): Promise<void>;
   restartEngine(): Promise<void>;
 }

 /** window.haloAgent（引擎传输，3 个方法）。
  *
  * 桥契约（dist 骨架屏根因修复）：request / pollEvents 返回 JSON 字符串，
  * 调用方（agent.ts）负责 JSON.parse。isReady 返回布尔，不变。
  */
 export interface HaloAgent {
   request(method: string, params: unknown): Promise<string>;
   pollEvents(sinceSeq: number): Promise<string>;
   isReady(): Promise<boolean>;
 }

export interface WindowState {
  maximized: boolean;
  minimized: boolean;
  width: number;
  height: number;
  dpi: number;
  titleBarHeight: number;
}

export interface ScreenInfo {
  bounds: [number, number, number, number];
  deviceName: string;
  primary: boolean;
}

export interface HostSystemInfo {
  osDescription: string;
  is64BitOs: boolean;
  is64BitProcess: boolean;
  isWindows7: boolean;
  cefVersion: string;
  hostVersion: string;
  dpi: number;
  isDev: boolean;
  isFirstRun: boolean;
  dataDir: string;
  cwd: string;
  gpuMode: string;
  screens: ScreenInfo[];
}

export interface PollResult {
  events: HaloFrame[];
  nextSeq: number;
}

/** 一帧（事件，由 P1 契约定义） */
export interface HaloFrame {
  t: 'evt';
  seq: number;
  k: string;
  p: unknown;
}

export interface EngineStatusPayload {
  state: 'starting' | 'ready' | 'restarting' | 'failed' | 'stopped';
  pid?: number;
  reason?: string;
}

export interface FilesDroppedPayload {
  paths: string[];
}

/** 宿主 → 前端的事件回调 */
export type HostEventKind =
  | 'host.windowState'
  | 'host.engineStatus'
  | 'host.filesDropped'
  | 'host.notice';

declare global {
  interface Window {
    haloHost?: HaloHost;
    haloAgent?: HaloAgent;
    /** 宿主通过 EvaluateScriptAsync 调用的全局函数 */
    __haloHostEvent?: (kind: HostEventKind, payload: unknown) => void;
    CefSharp?: {
      BindObjectAsync(...names: string[]): Promise<boolean | void>;
    };
    // P0 兼容桥（保留，P5 清理前不动）。
    hostBridge?: {
      getSystemInfoAsync: () => Promise<Record<string, unknown>>;
      enginePingAsync: () => Promise<{
        Ok: boolean;
        ElapsedMs: number;
        Raw?: string;
        Error?: string;
      }>;
    };
  }
}
