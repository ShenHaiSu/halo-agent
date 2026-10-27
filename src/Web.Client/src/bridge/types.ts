// src/Web.Client/src/bridge/types.ts
// D4-7 桥接类型声明：window.haloHost（11 方法）/ haloAgent（3 方法）/ 事件入口。
//
// 分层：bridge 只定义类型，不依赖任何业务模块。

/** window.haloHost（宿主业务接口，11 个方法） */
export interface HaloHost {
  windowMinimize(): Promise<void>;
  windowMaximizeOrRestore(): Promise<WindowState>;
  windowClose(): Promise<void>;
  windowState(): Promise<WindowState>;
  systemInfo(): Promise<HostSystemInfo>;
  openExternal(url: string): Promise<void>;
  showItemInFolder(path: string): Promise<void>;
  copyText(text: string): Promise<void>;
  readClipboardImage(): Promise<string | null>;
  toggleDevTools(): Promise<void>;
  restartEngine(): Promise<void>;
}

/** window.haloAgent（引擎传输，3 个方法） */
export interface HaloAgent {
  request(method: string, params: unknown): Promise<unknown>;
  pollEvents(sinceSeq: number): Promise<PollResult>;
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
