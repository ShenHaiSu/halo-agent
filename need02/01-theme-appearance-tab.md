# 01 · 设置中心首 Tab「主题和样式」施工方案

## 1. 现状盘点（已读代码）

- 入口：`SettingsCenterDrawer.vue` 全文件 81 行：`n-tabs` 写死 4 个 `n-tab-pane`（providers/network/storage/runtime），`activeTab` 类型为 4 联合，无外观 Tab；`defaultTab` prop 同为 4 联合；`App.vue` L111/L116 `settingsModalTab/openSettingsModal` 同为 4 联合。
- 主题现状三散点：
  1. `systemStore.ts` L19/L30-46：`themeMode: 'dark'|'light'` + `applyThemeClass/toggleTheme`，仅内存态，刷新丢失，无字号/字体/主题色/宽度概念；`App.vue` L121-124 由此计算 `activeTheme/activeThemeOverrides`，L138 硬编码 `applyThemeClass('dark')`。
  2. `theme/naiveTheme.ts`：`haloDarkThemeOverrides/haloLightThemeOverrides`，`fontFamily` 写死微软雅黑、`fontSize 13px`、`primaryColor` 暗 `#3b82f6`/亮 `#2563eb`，无参数化。
  3. `styles/main.css` L5-41：`:root` 暗 / `html.light` 亮两套 `--bg-*/--text-*` + `--font-sans/--font-mono`，`body{font-family:var(--font-sans)}`。
- 会话宽度现状：`MessageTimeline.vue` L3 `max-w-4xl mx-auto w-full px-4` 写死居中限宽，无占满选项。

## 2. 目标与非目标

- 目标：首 Tab 即「主题和样式」，至少可编辑：全局字体大小、中文字体、英文字体、暗/亮切换、主题色切换、会话主界面「限定最大宽度 vs 占满可用宽度」。切换即时预览 + 持久化 + 刷新恢复。
- 非目标：不动 Provider/网络/存储/运行时逻辑；不引入 electron 级系统字体枚举；不做按工作区独立外观。

## 3. 配置模型设计

### 3.1 `AppearanceSettings`（前端唯一真相源）

```ts
// 建议新建 src/Web.Client/src/theme/appearance.ts（类型+默认值+应用函数）
export type ChatWidthMode = 'centered' | 'full';
export interface AppearanceSettings {
  themeMode: 'dark' | 'light';
  themeColor: string;            // 主题色，预设 6 色 + 自定义 hex
  fontSize: number;              // 全局基准字号 px，12~16，默认 13
  chineseFont: string;           // 如 "Microsoft YaHei UI"
  englishFont: string;           // 如 "Segoe UI"
  monoFont: string;              // 固定保留可编辑（等宽），默认 Consolas 栈
  chatWidthMode: ChatWidthMode;  // 'centered' 限宽居中 | 'full' 占满
}
export const DEFAULT_APPEARANCE: AppearanceSettings = {
  themeMode: 'dark', themeColor: '#3b82f6', fontSize: 13,
  chineseFont: 'Microsoft YaHei UI', englishFont: 'Segoe UI',
  monoFont: 'Consolas', chatWidthMode: 'centered',
};
export const THEME_COLOR_PRESETS = [
  { label: '科技蓝', value: '#3b82f6' }, { label: '深邃蓝', value: '#2563eb' },
  { label: '青绿', value: '#10b981' }, { label: '紫', value: '#8b5cf6' },
  { label: '橙', value: '#f59e0b' }, { label: '玫红', value: '#f43f5e' },
];
export const CHINESE_FONT_OPTIONS = ['Microsoft YaHei UI','Microsoft YaHei','PingFang SC','SimSun','SimHei'];
export const ENGLISH_FONT_OPTIONS = ['Segoe UI','Inter','Arial','Helvetica Neue','system-ui'];
export const FONT_SIZE_OPTIONS = [12, 13, 14, 15, 16];
```

- 字段与需求一一对应，多给的 `monoFont` 为等宽完整性（主题文件本就有 `fontFamilyMono`），Tab 内可折叠为「高级」。
- `chatWidthMode` 语义：`centered` = 现状 `max-w-4xl mx-auto`；`full` = `max-width:none; width:100%`。

### 3.2 持久化：localStorage 优先（本次必做），后端文件可选（二期）

- 必做：`localStorage['halo.appearance.v1']` 存 JSON；`appearanceStore`（或直接扩展 `systemStore`）负责 `load()/save()/reset()` + `applyToDom()`。理由：外观是纯前端表现，无需 DPAPI/原子写盘；`systemStore` 当前本就无后端持久化，保持一致心智；CefSharp 离线可用。
- 二期可选：`{DataRoot}/config/appearance.json` + `get/saveAppearance` bridge；本次方案预留字段与函数签名，但不列入验收，避免扩大 C# 桥接面。

## 4. 状态与主题应用链路

```
AppearanceSettingsPanel（编辑） → appearanceStore.update+save
  → ① documentElement class light/dark（复用 systemStore.applyThemeClass，保持兼容）
  → ② documentElement.style.setProperty(--font-sans/--font-mono/--app-font-size/--accent-primary)
  → ③ App.vue activeThemeOverrides = buildOverrides(appearance)（替代静态 haloDark/Light）
  → ④ MessageTimeline 容器 class = chatWidthMode
```

- `systemStore.themeMode` 保留为兼容只读映射（`appearanceStore.themeMode` 双向同步），`WorkbenchTopBar` 的 `toggleTheme()` 改为调 `appearanceStore.toggleTheme()`，避免两处真相源打架。
- `naiveTheme.ts` 新增 `buildHaloThemeOverrides(appearance, isDark)` 工厂：以现有两套为基座，仅覆盖 `common.fontFamily/fontFamilyMono/fontSize/fontSizeMedium/fontSizeSmall/primaryColor(+hover/pressed/suppl 由 tinycolor 或预设映射推导)`。简单起见 hover/pressed 写死为同色系三档映射表（6 预设 × 暗亮各 3 值），自定义色则 hover=原色、pressed=原色（Naive 允许同值），避免引入颜色库。
- `main.css` 新增：
  ```css
  :root { --app-font-size: 13px; }
  body { font-size: var(--app-font-size); }
  .chat-timeline-centered { max-width: 56rem; margin-inline: auto; width: 100%; }
  .chat-timeline-full { max-width: none; width: 100%; }
  ```
  中英文字体合成：`--font-sans: "<chinese>", "<english>", PingFang SC, -apple-system, ...`，由 `applyToDom` 拼接后设置，`body` 自动跟随（已是 `var(--font-sans)`，无需改选择器）。

## 5. UI 设计（首 Tab 内容排版）

新建 `components/settings/AppearanceSettingsPanel.vue`，`n-card` 分 4 组，每组 `data-testid` 已给出：

1. **明暗与主题色**（`appearance-theme-card`）：`n-radio-group` 两选项（暗黑/浅色，带 Sun/Moon 图标）+ 6 色 `n-color-picker` 预设 swatch（`n-swatches` 或自定义 button 圆点，选中描边）+ 自定义 hex `n-input` + 校验。主题色即时改 `--accent-primary` 与 Naive `primaryColor`。
2. **字体**（`appearance-font-card`）：全局字号 `n-slider(12~16, marks)` + 数字回显；中文字体 `n-select`（选项+可自定义输入：用 `filterable + tag` 允许手输）；英文字体同式；等宽字体折叠 `n-collapse`。下方实时预览行：「Aa 中文测试 English 123」随改随变。
3. **会话主界面宽度**（`appearance-width-card`）：两张 `setting-option` 可选卡（沿用 need01 语义类）：「限定最大宽度·居中（阅读友好）」vs「占满可用宽度（宽屏利用）」，各配 mini 示意图（div 条模拟），`data-testid=chat-width-centered/full`。
4. **页脚**：`重置为默认`（secondary）+ 当前生效摘要（`13px · 微软雅黑 / Segoe UI · #3b82f6 · 居中限宽`）。

## 6. 施工步骤（含文件行级定位）

1. 新建 `src/Web.Client/src/theme/appearance.ts`（类型+默认值+预设+`buildFontStack(appearance)`+`buildHaloThemeOverrides()`）。
2. 新建 `src/Web.Client/src/stores/appearanceStore.ts`（或直接扩 `systemStore.ts` L19-46：推荐独立 store，`systemStore` 仅做代理转发，保持旧 import 不炸）：
   - `state: settings`；`load()` 读 localStorage+合并默认值+容错；`save()` 写盘；`reset()`；`applyToDom()`（class + CSS 变量）；`toggleTheme()`。
   - `App.vue` L137-141 `onMounted` 改为 `appearanceStore.loadAndApply()` 先于 `systemStore.refreshAll()`，删除硬编码 `applyThemeClass('dark')`（改为恢复用户值）。
3. 新建 `AppearanceSettingsPanel.vue`（§5 四组卡）。
4. 改 `SettingsCenterDrawer.vue`：首位插入 `<n-tab-pane name="appearance" tab="主题和样式"><AppearanceSettingsPanel/></n-tab-pane>`；`activeTab` 默认值改为 `'appearance'`；`defaultTab` 联合类型 += `'appearance'`（L57-68）；同步改 `App.vue` L111/L116 `settingsModalTab/openSettingsModal` 联合类型（保持传参兼容，旧 `'providers'` 仍可用）。
5. 改 `App.vue` L121-124：`activeThemeOverrides = buildHaloThemeOverrides(appearance, themeMode)`；`activeTheme` 逻辑不变。
6. 改 `main.css`：追加 §4 的 `--app-font-size/.chat-timeline-*`（Chrome109 安全）。
7. 改 `MessageTimeline.vue` L3：`:class="chatWidthMode==='full' ? 'chat-timeline-full' : 'chat-timeline-centered'"` 替代写死 `max-w-4xl mx-auto`，保留 `px-4 py-4 space-y-4`。
8. `hostBridge.ts` mock：无需改；如需浏览器 dev 预览，在 mock 初始值旁注释说明外观走 localStorage 即可。

## 7. 验收清单

- [ ] 打开设置中心默认即「主题和样式」首 Tab；`defaultTab` 传参旧值仍可定位到对应 Tab。
- [ ] 字号 12~16 拖动即时生效（含 Naive 组件字号 `fontSize*`）；中/英文字体切换 `body` 字体栈即时变化；刷新后恢复（localStorage 有值）。
- [ ] 暗/亮切换与顶栏 `toggleTheme` 双向同步，无闪烁；`html.light` 变量与 need01 语义类无回归。
- [ ] 6 预设主题色点击即换（含按钮/选中边/进度条 `statusColor` 建议保持语义色不变，仅主色跟随）；自定义 hex 非法输入有 warning 不崩。
- [ ] 会话宽度两种模式在宽/窄窗下均无横向滚动；`full` 模式代码块 `pre{overflow-x:auto}` 仍可横滚（仅块内）。
- [ ] `vue-tsc -b && vite build` 通过；`grep -rn "applyThemeClass('dark')" src` 仅剩 store 定义处。

## 8. 风险与回退

- Naive 自定义色 hover 推导简化：极端 hex 在亮暗下对比度可能不足——以预设 6 色为主推，自定义为高级入口，文档注明。
- 旧用户 localStorage 无值：`load()` 与 `DEFAULT_APPEARANCE` 深合并，未知字段忽略，保证向前兼容。
- 回退：删除首 Tab 并恢复 `activeTab` 默认 `'providers'` 即可，CSS 变量有默认值不影响旧快照。
