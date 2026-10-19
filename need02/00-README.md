# need02 施工方案总览（设置中心 / 边栏会话 / Provider 重设计）

> 输出路径：`docs/need02/`；只做方案，不改代码。
> 基线代码均已通读，行号为 2026-10-14 时刻的实际行号，施工前若有漂移请以符号名为准重新定位。

## 需求一览与文档拆分

| # | 需求 | 方案文档 | 主要触点文件 |
|---|------|----------|--------------|
| 1 | 设置中心最前新增「主题和样式」Tab：全局字号 / 中文字体 / 英文字体 / 暗亮切换 / 主题色 / 会话主界面宽度模式 | `01-theme-appearance-tab.md` | `SettingsCenterDrawer.vue`, `systemStore.ts`, `naiveTheme.ts`, `main.css`, `App.vue`, `MessageTimeline.vue`，新建 `AppearanceSettingsPanel.vue` + `appearanceStore.ts`（或并入 systemStore） |
| 2 | 「网络与代理」自动检测系统代理时下方 table cell 不能自动换行，样式修复 | `02-network-table-wrap-fix.md` | `NetworkProxySettings.vue` L109-130, `main.css` L86-90 |
| 3 | 左侧边栏会话卡：不展示 hash，展示创建 + 最后消息时间（简洁） | `03-sidebar-session-time.md` | `WorkspaceSessionSidebar.vue` L223-227 / L297-301, `workspaceStore.ts`, `hostBridge.ts` `SessionMetadata` |
| 4 | 会话删除二次确认：不用 Dialog，原地变「确定删除？」，第二下即确认 | `04-inline-delete-confirm.md` | `WorkspaceSessionSidebar.vue` L344-354 / L467-545 |
| 5 | 「模型与服务商」Provider 重设计：a. Key 占一行/代理独占一行；b. 预设头 `x-opencode-session → 会话hash`；c. Model 卡默认只读、点击排他展开编辑 + 预设按钮 + 自定义输入 | `05-provider-model-redesign.md` | `ProviderManagerView.vue` 全文件，`ProviderConfigService.cs`, `HeaderVariableResolver.cs`, `hostBridge.ts` mock/bridge |

## 全局施工原则（五篇通用）

1. **零后端破坏**：除 01 可选持久化、05b 仅前端默认值外，不动 C# DTO 字段名与 JSON 结构；Naive-UI 2.45.3 + CefSharp Chrome 109，只用 `var()`、`flex/grid`，不用 `:has()`、`container queries`、`color-mix()`。
2. **暗亮同测**：所有样式改动必须在 `html.dark(:root)` 与 `html.light` 下各截一图；沿用 `need01/05` 的语义类思想（`.setting-option/.setting-inset`），新增语义类同样写两套。
3. **不横溢**：Drawer 内保持 `min-w-0 max-w-full overflow-x-hidden` + `main.css:.settings-drawer-body *{min-width:0}`；任何 `font-mono` 长串必须 `break-all + overflow-wrap:anywhere`。
4. **可测性**：沿用现有 `data-testid` 命名（`provider-*`, `model-row-*`, `session-item-*`），新增 id 在各篇中已给出；`vue-tsc -b && vite build` 为合闸门槛。
5. **顺序建议**：02（纯 CSS，风险最低）→ 03+04（边栏同一文件，先后做避免冲突）→ 05（改动最大）→ 01（新增面最大，可独立并行）。

## 验收总表

- [ ] 02 无横向滚动条，长 URI/绕过列表在 1020px Drawer 与窄窗下均换行（见 02 篇 §5）。
- [ ] 03 边栏无任何 `Hash:` 字样（`grep -rn "Hash:" src/Web.Client/src/components/sidebar` 零命中），时间一行展示（见 03 篇 §5）。
- [ ] 04 无 `n-modal` 删除弹窗（该文件内 `n-modal/showDeleteConfirm` 删除），两下点击完成删除，Esc/切会话/3s 超时自动复位（见 04 篇 §5）。
- [ ] 05 API Key 与代理策略各占一行；新建 Provider 默认带 `X-Session-Hash` + `x-opencode-session`；Model 默认只读、同时最多展开一个；128k/256k/512k/1M 与 4k/8k/16k/50k/64k/128k 预设可点、可手输（见 05 篇 §7）。
- [ ] 01 首 Tab 即「主题和样式」，刷新后偏好保持，暗/亮/主题色/字号/字体/会话宽度即时生效无闪烁（见 01 篇 §7）。

## 与 need01 的关系

- 主题语义类、Drawer 防溢出、Stage 文案下线结论继续有效，不重复施工。
- 01 篇是 `need01/05` 的延续：把散落在 `naiveTheme.ts + main.css + systemStore.themeMode` 的主题能力收敛为用户可编辑的「外观设置」。
