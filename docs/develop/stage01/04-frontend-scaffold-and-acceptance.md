# Stage 01 - 04：Vite (`chrome109`) + Naive UI + Tailwind CSS 混合前端架构、交互样式规范与 Stage 01 验收标准

> **所属阶段**：Stage 01
> **关联规范**：[`docs/CEFSHARP_ARCHITECTURE_STANDARD.md`](../../CEFSHARP_ARCHITECTURE_STANDARD.md) 第五章、第六章第 2.5 节

---

## 一、 前端技术栈与版本锁定（方案 C：Naive UI 全量引入 + Tailwind CSS v3.4 混合模式）

### 1. 核心依赖选型与架构定位
由于桌面端前端构建产物随客户端打包并由本地 `app://halo.local` 内存/磁盘高速加载（零公网传输延迟），前端采用 **Naive UI 全量引入 + Tailwind CSS v3.4 原子化布局** 的混合架构：

* **包管理器**：`pnpm`（严格锁定 `pnpm-lock.yaml`）
* **核心框架**：`vue@^3.5` + `pinia@^2.2` + `vue-router@^4.4`
* **构建与语言**：`vite@^5.4` + `typescript@^5.6`（编译目标锁死 `chrome109`）
* **UI 组件库（全量注册）**：`naive-ui@^2.40`
  * **职责边界**：负责所有高复杂度交互组件——多级弹窗（`NModal` / `NDrawer`）、表单验证（`NForm` / `NFormItem`）、下拉与级联选择（`NSelect` / `NPopselect`）、动态表格与标签输入（`NDataTable` / `NDynamicInput`）、分栏拖拽容器（`NSplit`）、虚拟滚动列表（`NVirtualList`）、消息反馈（`NMessageProvider` / `NDialogProvider` / `NNotificationProvider`）、分段控制器（`NRadioGroup` / `NTabs`）、折叠面板（`NCollapse`）、气泡提示（`NTooltip` / `NPopover`）。
  * **引入方式**：在 `main.ts` 中直接 `import naive from 'naive-ui'; app.use(naive);` 全量注册，无需配置复杂的按需引入插件，消除开发态首次加载组件时的二次编译卡顿，保证组件样式与动效 100% 完整。
* **原子化样式与定制视图**：`tailwindcss@^3.4` + `postcss` + `autoprefixer`
  * **职责边界**：负责主工作台自由弹性布局、AI 消息气泡排版、代码块与 Unified Diff 逐行比对视图、终端模拟器面板、微交互动画与自定义状态徽标。
* **图标与富文本渲染**：
  * `lucide-vue-next`：**全站唯一图标方案（纯内联 SVG）**，严格遵守架构宪章第六章 2.5 条（Win7 无彩色 Emoji 字体，严禁使用任何 Emoji 字符充当图标）。
  * `markdown-it` + `highlight.js`：负责 AI 回复的流式 Markdown 与代码语法高亮渲染。

---

### 2. `vite.config.ts` 强制标准配置
```typescript
import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import path from 'path';

export default defineConfig({
  base: './',
  plugins: [vue()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    port: 5173,
    strictPort: true,
  },
  build: {
    // 铁律：锁定编译目标为 chrome109，自动降级高版本 JS/CSS
    target: 'chrome109',
    cssTarget: 'chrome109',
    outDir: '../Host.Desktop/wwwroot',
    emptyOutDir: true,
    sourcemap: false,
    assetsInlineLimit: 4096,
    chunkSizeWarningLimit: 2500, // 全量引入 Naive UI 属本地桌面端正常设计，放宽体积告警阈值
  },
});
```

---

## 二、 桌面 IDE 级视觉设计系统与 Naive UI + Tailwind 融合规范

### 1. Naive UI 与 Tailwind CSS v3.4 样式防冲突机制
Tailwind 默认的 `preflight` 会将 `<button>` 的背景色重置为 `transparent`，这会导致 Naive UI 的实心按钮（如 `<n-button type="primary">`）在部分层级顺序下背景透明。必须在样式加载层做好严格隔离：

1. **显式控制 CSS 注入顺序（`main.ts`）**：
   * 在根组件挂载前插入 `<meta name="naive-ui-style" />` 锚点标签，确保 Naive UI 的 CSS-in-JS 样式表始终插入在 Tailwind Base Reset 之后，优先级高于 Tailwind Reset。
2. **自定义全局样式与 Win7 字体栈（`src/styles/main.css`）**：
   ```css
   @tailwind base;
   @tailwind components;
   @tailwind utilities;

   :root {
     /* Win7 兼容字体栈：优先雅黑 UI/雅黑，等宽优先 Consolas */
     --font-sans: "Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
     --font-mono: "JetBrains Mono", "Cascadia Mono", Consolas, "Courier New", monospace;

     /* IDE 级暗黑主题语义色变量 (供 Tailwind 与自定义组件共享) */
     --bg-app: #0d1117;
     --bg-sidebar: #131820;
     --bg-surface: #181f29;
     --bg-elevated: #212a36;
     --border-subtle: #283241;
     --text-primary: #e6edf3;
     --text-secondary: #8b949e;
     --accent-primary: #3b82f6;
   }

   html.light {
     --bg-app: #f8fafc;
     --bg-sidebar: #f1f5f9;
     --bg-surface: #ffffff;
     --bg-elevated: #f8fafc;
     --border-subtle: #e2e8f0;
     --text-primary: #0f172a;
     --text-secondary: #64748b;
     --accent-primary: #2563eb;
   }

   body {
     font-family: var(--font-sans);
     background-color: var(--bg-app);
     color: var(--text-primary);
     user-select: none;
     overflow: hidden;
     margin: 0;
     padding: 0;
     -webkit-font-smoothing: antialiased;
   }
   ```

---

### 2. Naive UI 桌面紧凑型主题定制（`src/theme/naiveTheme.ts`）
Web 端组件库的默认尺寸（34px~40px 行高、大圆角、翠绿色主色）在桌面软件中会显得松散且缺乏专业 IDE 质感。我们通过 `GlobalThemeOverrides` 将 Naive UI 定制为 **Cursor / VS Code / Linear 风格的紧凑桌面工业风**：

```typescript
import type { GlobalThemeOverrides } from 'naive-ui';

export const haloDarkThemeOverrides: GlobalThemeOverrides = {
  common: {
    fontFamily: '"Microsoft YaHei UI", "Microsoft YaHei", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
    fontFamilyMono: 'Consolas, "JetBrains Mono", "Courier New", monospace',
    primaryColor: '#3b82f6',          // 科技蓝主色（替代 Naive 默认绿色）
    primaryColorHover: '#60a5fa',
    primaryColorPressed: '#2563eb',
    primaryColorSuppl: '#1d4ed8',
    infoColor: '#38bdf8',
    successColor: '#22c55e',
    warningColor: '#f59e0b',
    errorColor: '#ef4444',
    bodyColor: '#0d1117',
    cardColor: '#161c24',
    modalColor: '#181f29',
    popoverColor: '#212a36',
    tableColor: '#161c24',
    inputColor: '#0f141b',
    borderColor: '#283241',
    borderRadius: '6px',              // 桌面端克制圆角
    borderRadiusSmall: '4px',
    fontSize: '13px',                 // 桌面端标准紧凑字号
    fontSizeMedium: '13px',
    fontSizeSmall: '12px',
    heightMedium: '30px',             // 紧凑型控件高度
    heightSmall: '26px',
  },
  Button: {
    fontWeight: '500',
    paddingMedium: '0 12px',
  },
  Input: {
    color: '#0d1117',
    colorFocus: '#090d12',
    border: '1px solid #283241',
    borderHover: '1px solid #3b82f6',
    borderFocus: '1px solid #3b82f6',
  },
  Modal: {
    boxShadow: '0 20px 50px rgba(0, 0, 0, 0.65)',
  },
  Tabs: {
    tabFontSizeMedium: '13px',
    tabGapMediumBar: '20px',
  },
};
```

---

### 3. 全局桌面壳层架构与布局规划（`App.vue`）

顶层 `App.vue` 统一包裹 Naive UI 四大 Provider，并确立全屏桌面级固定视口结构（`100vw x 100vh`，禁止外层出现浏览器滚动条）：

```text
<n-config-provider :theme="activeTheme" :theme-overrides="activeThemeOverrides" :locale="zhCN" :date-locale="dateZhCN">
  <n-dialog-provider>
    <n-notification-provider placement="bottom-right">
      <n-message-provider>
        +-----------------------------------------------------------------------------------+
        | 1. 顶部全局导航与工作台状态栏 (TopHeaderBar: 高度 40px, 固定不滚动)                 |
        |    [侧栏折叠] [项目切换 NPopselect] / [当前会话名] | [模型选择 NSelect] [设置按钮] |
        +-----------------------------------------------------------------------------------+
        | 2. 主体可拖拽分栏区 (<n-split direction="horizontal" :min="0.16" :max="0.35">)     |
        |    +------------------------+---------------------------------------------------+ |
        |    | Pane 1: 左侧工作区导航 | Pane 2: 右侧主工作台 (Flex-1, 高度 calc(100vh-66px))| |
        |    | (默认宽度 260px)        |  - 消息时间轴滚动区 (独立自定义细滚动条)            | |
        |    | - 项目信息卡片         |  - ReAct 思考流 / ToolCall 折叠卡片 / Diff 视图     | |
        |    | - 新建会话 NButton     |  - 底部悬浮式多功能 Prompt Composer 输入舱          | |
        |    | - 会话分组列表         |                                                   | |
        |    +------------------------+---------------------------------------------------+ |
        +-----------------------------------------------------------------------------------+
        | 3. 底部遥测与系统状态栏 (StatusBar: 高度 26px, 仿 VS Code 底部状态条)              |
        |    [OS: Win7 SP1 / Win11] [存储: C:\HaloAgentData] [代理: SYSTEM] [SessionHash]   |
        +-----------------------------------------------------------------------------------+
      </n-message-provider>
    </n-notification-provider>
  </n-dialog-provider>
</n-config-provider>
```

---

## 三、 Stage 01 存储与系统诊断面板交互设计（`StorageSettingsPanel.vue`）

在 Stage 01 阶段，设置中心（基于 `<n-modal preset="card" style="width: 780px">` + `<n-tabs type="line" placement="left">`）率先落地 **“存储与日志”** 与 **“系统运行环境”** 两个标签页：

1. **存储路径管理卡片（`<n-card title="数据与日志根目录配置" size="small">`）**：
   * 使用 `<n-alert type="info">` 提示当前引导指针文件位置（`%LocalAppData%\HaloAgent\bootstrap.json`）。
   * **当前生效路径展示栏**：只读 `<n-input>` 显示当前路径（默认 `C:\HaloAgentData`）+ `<n-tag type="success">可读写 · 剩余空间 128.4 GB</n-tag>`。
   * **操作按钮组（`<n-space>`）**：
     * `<n-button type="primary" @click="handleChangeStoragePath">`（图标：`FolderSync`）：点击首先调用 `hostBridge.selectFolderDialog()` 弹出原生目录选择框；选定新路径后，弹出 `<n-modal preset="dialog">` 让用户通过 `<n-radio-group>` 选择：
       * **选项 A（默认推荐）**：`将当前目录下的所有历史会话、Provider 配置及日志完整迁移至新路径并切换`（显示 `<n-progress>` 进度加载态）
       * **选项 B**：`仅切换至新路径（初始化空白配置，保留原目录旧文件不动）`
     * `<n-button secondary @click="openInExplorer(dataRootPath)">`（图标：`ExternalLink`）：`在资源管理器中打开数据目录`。
     * `<n-button secondary @click="resetToDefaultCDrive">`：`恢复默认 C:\HaloAgentData`。
2. **三通道分级日志监控区（`<n-descriptions bordered :column="3">`）**：
   * 实时展示 `app-YYYYMMDD.log`（系统运行日志）、`llm-traffic-YYYYMMDD.log`（大模型请求审计）、`tool-exec-YYYYMMDD.log`（CMD/Python 工具执行日志）的文件大小，并提供一键 `[打开日志目录]` 按钮。

---

## 四、 前端 Bridge 通信封装（`src/bridge/hostBridge.ts`）

```typescript
export interface BridgeResponse<T> {
  ok: boolean;
  code?: string;
  message?: string;
  data?: T;
}

export interface SystemRuntimeInfo {
  osDescription: string;
  isWin7: boolean;
  architecture: string;
  dotnetVersion: string;
  cefSharpVersion: string;
  currentDataRoot: string;
  bootstrapFilePath: string;
  logsDirectory: string;
}

declare global {
  interface Window {
    CefSharp?: {
      BindObjectAsync: (name: string) => Promise<void>;
    };
    hostBridge?: {
      getSystemInfo: () => Promise<BridgeResponse<SystemRuntimeInfo>>;
      getStorageConfig: () => Promise<BridgeResponse<{ dataRootPath: string; defaultPath: string; diskFreeSpaceMb: number }>>;
      selectFolderDialog: (initialPath?: string) => Promise<BridgeResponse<string | null>>;
      changeStoragePath: (newPath: string, migrateData: boolean) => Promise<BridgeResponse<boolean>>;
      openPathInExplorer: (targetPath: string) => Promise<BridgeResponse<boolean>>;
    };
    __haloReceiveHostEvents?: (events: Array<{ type: string; payload: unknown; timestamp: number }>) => void;
  }
}

export async function initHostBridge(): Promise<void> {
  if (window.CefSharp && !window.hostBridge) {
    await window.CefSharp.BindObjectAsync('hostBridge');
  }
}
```

---

## 五、 Stage 01 验收标准（Acceptance Criteria）

完成 Stage 01 后，必须逐项通过以下验证方可进入 Stage 02：

| 验收编号 | 验收维度 | 具体测试步骤 | 预期通过标准 |
| :--- | :--- | :--- | :--- |
| **S01-AC01** | **构建与目标合规性** | 1. 在 `src/Web.Client` 执行 `pnpm build`<br>2. 在 `src/Host.Desktop` 执行 `dotnet build -c Release` | 1. 前端产物生成至 `src/Host.Desktop/wwwroot/index.html`<br>2. C# 编译 0 Error、0 Warning，目标框架确认为 `net6.0-windows`，CefSharp 版本为 `109.1.110` |
| **S01-AC02** | **Naive UI + Tailwind 渲染与协议加载** | 启动 `Host.Desktop.exe` 并检查 `window.location.href` 及界面样式 | 地址为 `app://local/index.html`（非 `file://`）；Naive UI 全量组件（`NModal`、`NSplit`、`NButton`、`NTabs`）、Tailwind 布局与 Lucide SVG 图标全部正常渲染，按钮无背景透明冲突，无 Emoji 豆腐块 |
| **S01-AC03** | **C盘默认存储与日志落盘** | 首次启动应用后，检查磁盘目录 | 1. `%LocalAppData%\HaloAgent\bootstrap.json` 自动创建<br>2. `C:\HaloAgentData`（或受限账户回退目录）自动创建 `config/`、`workspaces/`、`sessions/`、`logs/`、`cef-cache/`<br>3. `logs/app-YYYYMMDD.log` 内记录了启动、OS 版本与 TLS 初始化日志 |
| **S01-AC04** | **存储路径手动修改与迁移** | 在前端 Naive UI 设置弹窗中点击“修改存储目录”选择新目录（如 `C:\HaloAgentData_Test`），勾选“迁移已有数据并切换” | 1. 原生 WinForms 文件夹选择框在主线程正常弹出，无跨线程异常<br>2. 原有配置与日志完整复制到新目录，`bootstrap.json` 更新为新路径<br>3. 重启程序后自动从新路径读取配置并追加日志 |
| **S01-AC05** | **Win7 防崩溃开关验证** | 在程序根目录创建空文件 `disable-gpu.flag` 后重启应用 | 启动日志打印 `GPU Acceleration disabled`，命令行参数包含 `disable-gpu-compositing`，页面渲染正常不黑屏 |
| **S01-AC06** | **优雅退出无残留进程** | 关闭主窗口后检查任务管理器 | `Host.Desktop.exe` 与所有 `CefSharp.BrowserSubprocess.exe` 子进程在 2 秒内全部退出，无僵尸进程残留 |
