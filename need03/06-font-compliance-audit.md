# 06 · 字体设置全局遵守审计：是否生效、是否需重启、修复方案

## 1. 审计结论（先说答案，已读代码验证）

| 设置项 | 是否即时生效 | 是否需重启 | 是否全界面遵守 | 缺口 |
|---|---|---|---|---|
| 明暗 `themeMode` | 即时（`applyToDom` 切 `dark/light` class + `App.vue` L123-126 切 Naive theme） | 否 | 是 | 无 |
| 主题色 `themeColor` | 即时（`--accent-primary` + Naive `primaryColor` 系） | 否 | 是 | 自定义色 hover/pressed 回退原色（已知可接受） |
| 全局字号 `fontSize` | **部分即时** | **部分需“换页/重进”才像生效** | **否，大量硬编码绕过** | `body{font-size:var(--app-font-size)}` 只管继承字号的文本；凡写死 `text-[13px]/text-[12px]/text-[11px]` 的地方全部绕过 |
| 中文/英文字体 | 即时（`--font-sans` 重写 + Naive `fontFamily`） | 否 | **否，大量硬编码绕过** | `tailwind.config fontFamily` 写死雅黑栈、`naiveTheme` 写死字体、`font-mono` 多处未用 `var(--font-mono)` |
| 等宽字体 `monoFont` | 即时（`--font-mono` + Naive `fontFamilyMono`） | 否 | **否** | `MessageTimeline` scoped `font-family: ui-monospace, Consolas` 写死（L226），`markdown-body code` 未跟变量 |

即：**不需要重启**（`appearanceStore.update → save + applyToDom` 全同步，`App.vue onMounted loadAndApply` 只管首屏），但用户会感觉“调了没用”，是因为**硬编码字号/字体族绕过了变量**——这是本篇要修的。

## 2. 不遵守清单（grep 实证）

- **字号硬编码**：`MessageTimeline.vue` L17 `text-[13px]`、L37/L46/L92/L105 `text-[12px]/text-[13px]`、L58/67 telemetry `text-[11px]`、L78 system `text-[11px]`；`ToolCallCard.vue` 全文件 `text-[11px]/text-xs`；`App.vue` 状态栏 `text-[11px]`；`naiveTheme.ts` `fontSize 13/12px` 写死（被 `buildHaloThemeOverrides` 覆盖所以 Naive 侧实际遵守，**但 `Tabs.tabFontSizeMedium 13px` 未被覆盖** → Tab 字号不跟）。
- **字体族硬编码**：`tailwind.config.js` `fontFamily.sans/mono` 写死（Tailwind 生成的 `font-sans/font-mono` 类不跟设置）；`MessageTimeline.vue` L226 `font-family: ui-monospace, Consolas` 写死；`main.css` `:root --font-sans/--font-mono` 初始值写死（首屏闪一下默认字体，`loadAndApply` 后才纠正，有 FOUC）。
- **字号不跟随的机制**：Tailwind `text-[13px]` 是绝对值，不继承 body；`body{font-size:var(--app-font-size)}` 只能影响 `text-xs/text-sm` 等相对类与裸文本。所以 12→16 档切换时，消息正文（13px 写死）纹丝不动 → 用户断定“设置无效”。

## 3. 修复设计：三层收敛（不追求 100% px 跟随，追求“可感知 + 可维护”）

### 3.1 策略：相对单位化（推荐 em 方案，而非全改 var）

- 正文类字号改相对：`text-[13px] → text-[length:calc(var(--app-font-size))]` 太重；更轻的是定义 `.app-text-*` 语义类：
```css
.app-text-body { font-size: var(--app-font-size); }
.app-text-small { font-size: calc(var(--app-font-size) - 1px); }
.app-text-mini { font-size: 11px; } /* telemetry/时间戳等装饰字，允许不跟 */
```
- 划分：**正文/输入/标题跟随**（body/small），**装饰字（时间戳/telemetry/badge）不跟**（mini 固定 11px，避免 16 档下时间行撑爆侧栏）。本次修前者，不动后者（并在文档注明，避免未来误“全改”）。
- Naive 侧：`buildHaloThemeOverrides` 已覆盖 `fontSize/fontSizeMedium/fontSizeSmall` ✔；补 `Tabs.tabFontSizeMedium/tabFontSizeSmall` 同步跟随（`appearance.ts` L138-164 加两行）。

### 3.2 字体族收敛

- `tailwind.config.js` 改 `fontFamily.sans: ['var(--font-sans)']`？Tailwind 不支持 var 数组——正确做法：**不动 config**，全局加：
```css
body, .font-sans { font-family: var(--font-sans) !important; }
code, pre, .font-mono { font-family: var(--font-mono) !important; }
```
`!important` 压过 Tailwind 生成类，一处收敛全站生效（含 CefSharp 首屏前 `loadAndApply` 即执行，无需重启）。
- 删 `MessageTimeline.vue` L226 硬编码 `font-family`，改为继承（`font-family: inherit` 或直接删该行，靠全局 `.font-mono` 生效）。
- `naiveTheme.ts` 写死的 `fontFamily/fontFamilyMono` 保留作**首屏 fallback**（`App.vue` 首帧在 `loadAndApply` 前用它，避免无字闪烁），注释标明“会被 appearance 覆盖”。

### 3.3 首屏与持久化（解释“是否要重启”）

- 现状已持久化（`localStorage['halo.appearance.v1']` + `loadAndApply`），重启后保持；**无需重启生效**。为防“用户以为要重启”，在外观面板页脚 `appearance-summary` 后加一行小字 `即时生效 · 已自动保存`（纯文案，零逻辑）。
- 若未来加后端漫游持久化（多机同步），再议；本次不动。

## 4. 施工步骤

1. `main.css`：加 `.app-text-body/small/mini` 三类 + `body/.font-sans/.font-mono` 变量收敛（§3.2）；`--app-font-size` 保留。
2. `MessageTimeline.vue`：正文两处 `text-[13px]` → `app-text-body`；思考/用户泡泡 `text-[12px]` → `app-text-small`；telemetry/system/时间戳 `text-[11px]` **保留**（装饰字）；删 scoped `font-family` 硬编码；`markdown-body code` 加 `font-family: var(--font-mono)`。
3. `ToolCallCard.vue`：header 函数名/正文 `text-xs` → `app-text-small`；输出块 `text-[11px]/text-xs` → `app-text-small` + `font-mono`（mono 已被全局收敛）；diff 统计数字保留 `text-[11px]`（装饰）。
4. `appearance.ts` `buildHaloThemeOverrides`：加 `Tabs: { tabFontSizeMedium: ..., tabFontSizeSmall: ... }` 跟随字号；`App.vue` 不动（已正确消费）。
5. `AppearanceSettingsPanel.vue` 预览区已有 `:style="{fontSize, fontFamily}"` ✔ 不动；页脚加 `即时生效` 文案。
6. 全站复查：`grep -rn "text-\[1[123]px\]|font-family:" src/Web.Client/src --include="*.vue" --include="*.css"` 逐条判定 body/small/mini，不批量替换。

## 5. 验收清单

- [ ] 12→16 拖动：消息正文/输入框/标题肉眼变大，时间戳/telemetry 不变（装饰字冻结符合预期，并在测试记录注明）。
- [ ] 中英字体切换（如改 `SimHei` / `Arial`）：正文与 Naive 控件同步变化，`code/pre` 跟等宽设置；DevTools 看 `body{font-family}` 即 `--font-sans` 当前值。
- [ ] 刷新/Cef 重启后设置保持（localStorage），首屏无字体闪烁；全程无“需重启”提示。
- [ ] `grep` 复查无裸 `font-family:` 硬编码残留（`naiveTheme` fallback 与注释除外）；`vue-tsc -b && vite build` 通过；暗/亮 + 12/16 四象限截图。

## 6. 风险与回退

- `!important` 收敛可能压过业务特殊字体（如需某处固定 Consolas 演示）→ 该处用行内 `:style` 显式覆盖并注释 `/* 固定字体，不跟随设置 */`，白名单管理。
- 字号相对化后 16 档下侧栏/卡片可能变高 → 用 16 档全页面走查一遍（侧栏时间行 mini 固定 11px 已防爆，消息区允许变高）。
- 回退：恢复 `text-[13px]` 三处 + 删全局收敛两行，一分钟可逆；`appearance-summary` 文案回退零风险。
