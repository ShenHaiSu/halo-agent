# need03 施工方案总览（会话卡时间 / 菜单内删除确认 / 自动标题 / 明暗代码样式 / 字体滑块重叠 / 字体全局遵守）

> 输出路径：`docs/need03/`；只做方案，不改代码。
> 基线为 2026-10-15 时刻实际代码；行号以符号名为准，施工前重新定位。
> 与 `docs/need02/` 的关系：03/04 篇是 need02 同名方案的**问题复盘 + 二次施工**，不是推翻；其他篇为新增。

## 需求一览与文档拆分

| # | 需求 | 方案文档 | 主要触点文件 |
|---|------|----------|--------------|
| 1 | 会话卡第一行会话名，第二行应为创建+最后活跃时间，但前端没展示出来 | `01-sidebar-session-time-fix.md` | `WorkspaceSessionSidebar.vue` L226-230/L319-323, `timeFormat.ts`, `hostBridge.ts` `SessionMetadata`, `WorkspaceSessionService.cs`, `SessionRepository.cs` |
| 2 | 删除二次确认改成：菜单保持展开，最后一项变红、文案变“确定删除”，原地不动再点一次即确认 | `02-menu-inline-delete-confirm.md` | `WorkspaceSessionSidebar.vue` L235-263/L328-356, `getSessionDropdownOptions` L536-568, `handleSessionMenuSelect` L570-603 |
| 3 | 默认标题两阶段：首条消息发出即取前 5 字；首轮响应结束后用首轮全文+提示词向同模型发小请求，解析结构化结果赋值，此后不再自动改 | `03-session-auto-title.md` | `workspaceStore.appendMessage` L296-318, `WorkspaceSessionService.AppendSessionMessageAsync` L236-308, `agentStore.sendPrompt/finishTurn`, `LlmGatewayClient.cs`, 新增 `sessionTitle.ts` + 后端 `GenerateSessionTitleAsync`（可选） |
| 4 | 暗黑→明亮切换时代码块与 tool 结果样式不跟随：tool 结果浅色字看不清、代码块黑底配深色字无法阅读 | `04-code-tool-theming-fix.md` | `MessageTimeline.vue` L25-41/L66-75/L84-95/L216-231, `ToolCallCard.vue` L57-111, `main.css` `:root/html.light` |
| 5 | 设置与诊断中心主题样式：全局字号滑块与下方中文字体 label 纵向重叠，查因并修复、防复发 | `05-font-slider-overlap-fix.md` | `AppearanceSettingsPanel.vue` L68-82, `main.css` `.settings-drawer-body`, Naive `n-slider` + `marks` |
| 6 | 全界面文字是否遵守字体设置、是否需重启才生效；不遵守则设计修复 | `06-font-compliance-audit.md` | `App.vue` L123-126, `appearanceStore.applyToDom` L51-68, `appearance.ts` `buildFontStack/buildHaloThemeOverrides`, `naiveTheme.ts`, `tailwind.config.js`, `MessageTimeline.vue`, `ToolCallCard.vue` |

## 全局施工原则（六篇通用）

1. **零破坏**：不动 C# DTO 字段名与 JSON 结构（除 03 可选新增后端接口）；Naive-UI 2.45.3 + CefSharp Chrome 109，只用 `var()`、`flex/grid`，不用 `:has()`、`container queries`、`color-mix()`。
2. **暗亮同测**：凡动样式必须 `html.dark` 与 `html.light` 各截一图；硬编码色一律收敛为语义类 / CSS 变量。
3. **不横溢**：Drawer 内保持 `min-w-0 max-w-full overflow-x-hidden`；长串 `break-all + overflow-wrap:anywhere`。
4. **可测性**：沿用 `data-testid`（`session-item-*`, `confirm-delete-btn`, `appearance-*`, `tool-call-card`, `message-timeline`）；合闸 `vue-tsc -b && vite build`，后端改动另加 `dotnet build`。
5. **顺序建议**：05（纯 CSS，最小）→ 01（排查为主）→ 04（样式收敛）→ 06（字体审计，依赖 05 结论）→ 02（菜单交互）→ 03（链路最长，前后端协同，最后做）。

## 验收总表

- [ ] 01：每卡第二行可见 `MM-DD · 活跃 x分钟前`，hover 有完整 tooltip；脏数据不崩（见 01 篇 §5）。
- [ ] 02：点删除后菜单不关闭、末项变红“确定删除”，原地再点即删；Esc/切卡/超时复位；无 `n-modal` 回潮（见 02 篇 §5）。
- [ ] 03：首条发出即 5 字标题；首轮结束后同模型标题请求回写一次；手动改名后永不覆盖；失败静默不打断（见 03 篇 §6）。
- [ ] 04：明亮模式下 tool 输出与代码块对比度 ≥ 4.5:1 目测可读，暗黑模式保持现有观感（见 04 篇 §5）。
- [ ] 05：12–16 全档下滑块与下方 label 无重叠，Drawer 无横向滚动（见 05 篇 §4）。
- [ ] 06：字号/中英/等宽三项即时生效、无需重启；全界面无硬编码字体族残留（见 06 篇 §5）。
