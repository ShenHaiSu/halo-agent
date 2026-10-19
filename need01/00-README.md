# need01 施工方案总览（README）

> 路径：`docs/need01/` ｜ 状态：仅方案，不施工 ｜ 适用分支：当前 `halo-agent` 主干
> 相关前端根目录：`src/Web.Client/src/`

## 1. 需求三件套

| # | 需求 | 一句话方案 | 详细文档 |
|---|------|-----------|---------|
| 1 | 主界面右上角上下文预算监控，常态下出现两个百分比，左侧大号百分比浮动溢出容器 | 只保留右侧 `used/total (xx%)` 文本，关闭左侧 `n-progress[type=circle]` 的内置指示器 | `01-context-quota-meter.md` |
| 2 | 主界面文本输入框底栏左侧的「当前项目文件夹名 + 默认Runner」信息删除 | 删除 `PromptComposer.vue` 底栏左半区 DOM 与其专属 computed/import，父容器改为右对齐 | `02-prompt-composer-footer.md` |
| 3 | 设置与诊断中心彻底重构：`Dialog` 居中太小 → 改为右侧滑入 `Drawer`，并解决内部溢出、亮暗主题 active 反转、`Stage xx` 文本下线 | 新建/替换为 `SettingsCenterDrawer.vue`（`n-drawer placement=right`），同步做防溢出、语义化选中态、文案清洗 | `03-settings-drawer-overview.md` + `04-settings-drawer-layout-overflow.md` + `05-settings-drawer-theme-stage.md` |

## 2. 触及文件清单（只读分析结论）

```
src/Web.Client/src/components/workbench/WorkbenchTopBar.vue  # 任务1，L35-L53
src/Web.Client/src/components/chat/PromptComposer.vue         # 任务2，L17-L27，L52-L68
src/Web.Client/src/components/settings/SettingsCenterModal.vue# 任务3，外壳（拟重命名/替换）
src/Web.Client/src/components/settings/ProviderManagerView.vue# 任务3，溢出重灾区 + 选中态硬编码
src/Web.Client/src/components/settings/NetworkProxySettings.vue#任务3，grid-cols-3 + descriptions溢出 + 选中态硬编码
src/Web.Client/src/components/settings/StorageSettingsPanel.vue#任务3，descriptions column=3 + Stage日志文案
src/Web.Client/src/components/settings/SystemRuntimePanel.vue # 任务3，Stage标题 + bg-black/15
src/Web.Client/src/App.vue                                    # 任务3，引用点 L76-L80，L101-L119
src/Web.Client/src/styles/main.css                            # 任务3，新增语义化选中态 token
src/Web.Client/src/theme/naiveTheme.ts                        # 任务3，只读确认，无需改（已有亮暗两套）
src/Web.Client/src/stores/systemStore.ts                      # 任务3，只读确认（themeMode dark|light + html.light类）
```

> `naive-ui` 实装版本为 `2.45.3`（见 `pnpm-lock.yaml`），`n-drawer` 可用，无需加依赖。
> 构建目标锁定 `chrome109`（见 `vite.config.ts`），方案中所有 CSS 均只用 chrome109 安全子集。

## 3. 施工顺序（建议一次一个 PR / 一次一验证）

1. 任务1（1 行级改动，零风险）→ 启动 `pnpm dev` 肉眼确认徽标常态。
2. 任务2（删除式改动，注意死代码清理）→ 确认发送/停止按钮右对齐、无横向抖动。
3. 任务3（重构，工作量最大）：
   1. 先做外壳 `Modal → Drawer` 替换并跑通开/关；
   2. 再逐面板做防溢出（按 `04` 文档的清单逐项勾）；
   3. 再做主题语义化 + `Stage` 清洗（按 `05` 文档）；
   4. 最后做亮/暗双主题 + 窄窗回归。

## 4. 全局验收标准

- [ ] `vue-tsc -b && vite build` 通过（`target: chrome109` 不变）。
- [ ] 暗黑/浅色主题各看一遍，无深色残留、无白色 hover 失效。
- [ ] 设置中心在 1280px 与 1600px 宽度下均无横向滚动条；长 URL / 长路径只会换行或省略，不会撑破容器。
- [ ] 全局搜索 `Stage` 在 `src/` 的用户可见层零命中（允许 `docs/` 历史文档保留）。
- [ ] 既有 `data-testid` 全部保留：`context-quota-meter-badge`、`prompt-composer`、`provider-manager-panel`、`network-proxy-settings-panel` 等。

## 5. 明确不做的事（防范围蔓延）

- 不改预算计算逻辑（`usagePercentage`、`statusColor` 阈值 70/90 保持）。
- 不改 `hover popover` 详情卡内容（任务1只动常态徽标）。
- 不引入新依赖、不升级 `naive-ui`、不改 `C#` 宿主与 `hostBridge`。
- 不顺手重排设置中心 Tab 顺序、不加新功能。
