# need19-06 施工顺序、文件清单、回归红线与验收

> 把 01~05 的设计变成可执行的工单。**按 P0 → P3 顺序施工**，每个阶段都能独立编译、独立验收、独立回滚。

---

## 1. 施工分期

```text
P0 骨架（0.5d）   04 → uiStore + RightRailDrawer 空壳 + Trigger + WorkbenchView 挂载
                  准出：能开能关、点外部不关、X 可关、宽度可拖并持久化
P1 锚点能力（0.5d）03（不含子会话）→ useTimelineNav + buildTimelineEntries 抽取 + Timeline 改造
                  准出：控制台可手动 `nav.scrollToAnchor(key)` 精准落点，自动滚底行为不变
P2 目录面板（1d）  05 → historyToc 纯函数 + HistoryTocPanel + HistoryBubble + 分批渲染
                  准出：气泡全量可用、点击跳转、过滤、键盘、500 条流畅
P3 Goal 面板（0.5d）02 → GoalPanelContent 抽取 + GoalFloatPanel 改造 + watchSession 迁移 + 幂等化
                  准出：G1~G9 全绿；浮卡与抽屉并存无重复监听
P4 收尾（0.5d）   03 §7 子会话跳转 + 05 §5.2 反向联动（可选）+ 边界（07）+ 全量回归
```

> 合计约 3 人日。P0 可先合入（用户能先看到"侧栏存在"），P1~P3 依次合入。

## 2. 文件改动清单（逐项可 review）

### 2.1 新增

| 文件 | 行数估算 | 依赖 | P |
|---|---|---|---|
| `src/stores/uiStore.ts` | ~90 | pinia | P0 |
| `src/components/rightRail/RightRailDrawer.vue` | ~180 | naive-ui, uiStore, lucide | P0 |
| `src/components/rightRail/RightRailTrigger.vue` | ~70 | uiStore, goalStore, lucide | P0 |
| `src/components/rightRail/PanelBoundary.vue` | ~30 | vue（`onErrorCaptured`） | P0 |
| `src/composables/useTimelineNav.ts` | ~120 | vue | P1 |
| `src/composables/useTimelineScroll.ts` | ~110 | vue, uiStore | P1 |
| `src/utils/historyToc.ts` | ~180 | 纯函数 | P2 |
| `src/components/rightRail/HistoryTocPanel.vue` | ~260 | uiStore, workspaceStore, subSessionStore, useTimelineNav | P2 |
| `src/components/rightRail/HistoryBubble.vue` | ~90 | naive-ui (tooltip) | P2 |
| `src/components/goal/GoalPanelContent.vue` | ~260 | goalStore, naive-ui | P3 |

### 2.2 修改

| 文件 | 改动点 | 风险 | P |
|---|---|---|---|
| `src/views/WorkbenchView.vue` | `provide(useTimelineNav)`、挂 `RightRailTrigger`/`RightRailDrawer`、宿主 ref | 低 | P0 |
| `src/components/chat/MessageTimeline.vue` | 锚点 ref 绑定、`setContainer`、`buildTimelineEntries`、滚底逻辑迁 `useTimelineScroll`、跳转锁判定 | **中**（主界面核心） | P1 |
| `src/utils/turnGroup.ts` | 追加 `TimelineEntry` + `buildTimelineEntries()` | 低（纯新增） | P1 |
| `src/stores/goalStore.ts` | `watchSession()` 幂等、`load()` 序号抑制 | 低 | P3 |
| `src/components/goal/GoalFloatPanel.vue` | 内部改用 `GoalPanelContent`、移除 `watchSession()` 调用 | **中**（既有验收项） | P3 |
| `src/styles/main.css` | `.rail-tab` / `.toc-bubble` / 锚点闪烁 keyframes | 低 | P0/P2 |
| `src/components/sub/SubHistoryView.vue` | 容器上报 + 锚点 ref（可选） | 低 | P4 |

### 2.3 **禁止改动**

* `src/App.vue`（布局、split、状态栏、SettingsCenterDrawer 挂载）
* `src/bridge/hostBridge.ts`（不新增 RPC）
* `src/stores/workspaceStore.ts` / `subSessionStore.ts`（只读消费）
* `develop/src/Host.Desktop/**`（后端零改动）
* `vite.config.ts`（target 保持 chrome109）

## 3. 回归红线（合并前必须逐条确认）

| # | 红线 | 验证方法 |
|---|---|---|
| R-01 | 主界面像素无变化（Drawer 关闭时） | 截图对比：`MessageTimeline` 首屏、`PromptComposer` 位置、`GoalFloatPanel` 位置、状态栏 |
| R-02 | 自动滚到底行为不变 | 打开会话 → 自动到底；发送消息 → 跟随到底；Drawer 关闭状态下完全一致 |
| R-03 | `timelineItems` 输出逐项相等 | 临时脚本：新旧实现同一 `sessionMessages` 下 `JSON.stringify` 比较（或 dev 环境下 console.assert） |
| R-04 | 所有既有 `data-testid` 仍存在且可点 | `goal-float-panel / goal-mini-btn / goal-expand-btn / goal-collapse-btn / goal-minimize-btn / goal-save-btn / goal-reset-btn / goal-progress-bar / goal-milestone-item / compact-session-btn / open-settings-btn / message-timeline / prompt-composer / composer-send …` |
| R-05 | 右键菜单/重放/分支/删除 不受影响 | 在 Timeline 上右键 → 复制/重放/分支/删除均正常（Drawer 关闭时） |
| R-06 | 图片缩略图懒加载不受影响 | 含图消息正常显示；`msg-images` testid 存在 |
| R-07 | 子会话只读视图不受影响 | 进入子会话 → `SubHistoryView` 正常，返回主会话正常 |
| R-08 | 压缩上下文后目录自动刷新 | 点顶栏「压缩上下文」→ 目录条目变化、`loadSessionMessages` 后不报错 |
| R-09 | 设置中心仍可打开 | 顶栏「设置」→ Drawer 正常（与右侧栏可共存） |
| R-10 | 无新增 console error/warn | 全程 devtools Console 干净（允许既有的 `[markdown-link] open failed` 之类） |

## 4. 纯函数测试（无测试框架的落地方式）

项目**没有**安装 Vitest/Jest（`package.json` 仅 `dev/build/preview`），本期**不引入**。采用「双导出 + node 断言脚本」：

```text
develop/src/Web.Client/scripts/
├─ toc.selfcheck.mjs        # node scripts/toc.selfcheck.mjs  （编译期零依赖）
└─ timeline-entries.selfcheck.mjs
```

因为 `historyToc.ts` / `buildTimelineEntries` 是纯 TS + 只有 `import type`，可用 `node --experimental-strip-types`（Node ≥22）或 `npx tsc --outDir .tmp` 后跑。**推荐最稳妥的写法**：

`package.json` 增加（可选，不改变现有构建）：

```json
"scripts": {
  "selfcheck": "node --experimental-strip-types scripts/toc.selfcheck.mjs"
}
```

> 若团队 Node 版本 < 22（本项目检测到 Python 3.14 与本机环境，但 Node 版本需现场确认：`node -v`），改用 `npx tsx`（需装包，不允许）→ **退化为手工清单**：把 §4.2 的用例写成 `scripts/toc.selfcheck.mjs` 里手抄的核心逻辑断言（重复实现风险高，不推荐）。
>
> **最终建议**：本期用「浏览器控制台临时断言」+ 手工验收表；把「引入 Vitest」作为独立 need 提出（不夹带在本期）。

### 4.1 必测纯函数用例（写在文档里，供实现对照）

| # | 函数 | 输入 | 期望 |
|---|---|---|---|
| U1 | `bubbleLabel('你好世界，这是一段超过十五个字的中文文本')` | — | `你好世界，这是一段超过十五个…`（15 码点 + …） |
| U2 | `bubbleLabel('短文本')` | — | `短文本`（无省略号） |
| U3 | `bubbleLabel('👨‍👩‍👧家庭')` | — | 不劈半（长度 = 码点数） |
| U4 | `toDisplayText('# 标题\n\n正文 **加粗** [链接](http://a)')` | — | `标题 正文 加粗 链接` |
| U5 | `toDisplayText('![图](a.png)')` | — | `[图片]` |
| U6 | `toDisplayText('```ts\nconst a = 1;\n```')` | — | `const a = 1;` |
| U7 | `toDisplayText('<script>alert(1)</script>hi')` | — | `hi`（标签被去） |
| U8 | `buildHistoryToc(entries, {role:'all',roundHeadsOnly:false})` | user×2 + assistant×3 + system×1 | 6 条，index 1..6，round 正确 |
| U9 | 同上 + `roundHeadsOnly:true` | 同上 | 2 user + 2 assistant（每轮首条），system 被过滤 |
| U10 | 孤立 tool 组 entry | — | role=assistant，label 以 `🔧` 开头 |
| U11 | `buildTimelineEntries(messages, groups)` | 与 `MessageTimeline` 现有 `timelineItems` 同输入 | 逐项 `key/kind` 相等（R-03 的自动化版本） |

## 5. 全量验收矩阵（手工，发布前必跑）

| # | 场景 | 期望 | 关联 |
|---|---|---|---|
| M1 | 首次启动 | 右侧无 Drawer；触发按钮可见；主界面与今日一致 | 00 R1 |
| M2 | 点触发按钮 | Drawer 滑入；rail 两个 icon；默认 Tab=目录 | 04 D1 / Q7 |
| M3 | 点遮罩 20 次 | 不关闭 | **R6 核心** |
| M4 | 点 X | 关闭 | **R7 核心** |
| M5 | 再点触发按钮 | 关闭 | 04 D4 |
| M6 | 打开 Drawer 时触发 AskUser 弹窗 | 弹窗在最上层可答 | 04 D12 |
| M7 | 目录气泡对齐 | user 右 / assistant 左 | 05 J1 |
| M8 | 任意气泡文本 | 15 字 + … | 05 J2 |
| M9 | 点击气泡 | 跳到对应消息，顶部 12px，闪烁 1.2s | 03 A1 |
| M10 | 流式中点击气泡 | 不被拽回底部 | 03 A3 |
| M11 | 500 条会话 | 打开 ≤1s，滚动流畅 | 05 J4 |
| M12 | Goal Tab | 显示当前目标 | 02 G1 |
| M13 | Goal 编辑保存 | 保存成功、浮卡同步 | 02 G2 |
| M14 | Goal 里程碑勾选 | 自动保存、重开保留 | 02 G4 |
| M15 | 子会话模式 | 目录显示子会话条目（或禁用提示）；Goal 只读父目标 | 03 A6 / 02 G5 |
| M16 | 切换会话 | 目录/Goal 均即时刷新 | 05 J6 / 02 G8 |
| M17 | 压缩上下文 | 目录刷新且点击正常 | 03 A9 |
| M18 | 窗口 1024×700 / 1920×1080 | 布局不破 | 04 D10 |
| M19 | 亮色主题 | 所有新 UI 配色正确、无"白底白字" | 01 §5.3 |
| M20 | Win7 x86 + CefSharp 109 | 启动、Drawer、跳转全部可用（无 `:has()` 等新特性） | 00 §4.7 |

## 6. 命令与门禁

```cmd
:: 前端类型检查 + 构建（本项目唯一前端门禁）
cd develop\src\Web.Client
call pnpm exec vue-tsc -b
call pnpm build

:: 一键构建（含前端 + 后端 publish）
cd develop
build-dist.cmd

:: 后端零改动，但仍建议跑一遍确认没被误改
dotnet build HaloAgent.sln -c Debug
```

* **合入条件**：`vue-tsc -b` 零错误（注意项目 `tsconfig` 开启了 `strict`，`any` 需显式）；`pnpm build` 成功；R-01~R-10 全过；M1~M20 至少 Win10 全过、Win7 抽测 M1/M3/M4/M9/M12/M20。
* **体积**：新增组件应让 `wwwroot` 主 chunk 增量 < 40KB（gzip 前），若超说明误引入了大依赖（如整包 lodash）。

## 7. 回滚方案

| 回滚粒度 | 操作 | 影响 |
|---|---|---|
| 只关功能 | `uiStore` 增加 `rightRailEnabled` 常量 `false`，`WorkbenchView` 用 `v-if` 屏蔽 `RightRailTrigger`/`RightRailDrawer` | 回到今日 UI；P1 的锚点能力留在代码里但无入口（无害） |
| 回滚 P3（Goal） | `GoalFloatPanel` 还原（保留原文件版本），`GoalPanelContent` 不动，Drawer 的 goal tab 显示「本期未启用」 | 浮卡行为 100% 还原 |
| 回滚 P1（Timeline） | `MessageTimeline` 还原（含用回内联 `timelineItems` 与内联滚底），`useTimelineNav` 保留但无注册 | 主界面 100% 还原；目录跳转失效（面板需同步隐藏） |
| 全量回滚 | 删除 10 个新增文件 + 还原 6 个修改文件 | 仓库回到基线 |

> 因此**每次提交（commit）应以 P 阶段为单位**，便于 `git revert` 单点回滚。建议 commit 粒度：`need19 P0 drawer shell` / `P1 anchor nav` / `P2 toc panel` / `P3 goal panel` / `P4 polish`。

## 8. 施工自查清单（提交前逐项打勾）

- [ ] 新增文件均有文件头注释（含 `need19` 标记与职责一句话）
- [ ] 所有颜色/字号走 CSS 变量或 Tailwind 语义 token，无硬编码 `#xxxxxx`
- [ ] 所有交互元素有 `data-testid`
- [ ] 没有 `console.log` 残留（`console.warn/error` 仅错误路径）
- [ ] 没有 `any`（除既有代码风格的兼容转换）
- [ ] `GoalFloatPanel` 的 testid 全部保留
- [ ] `MessageTimeline` 的模板结构变化仅限增加 `:ref` 属性
- [ ] Drawer 关闭时 `document.querySelector('[data-testid="right-rail-drawer"]') === null`（`displayDirective='if'`）
- [ ] 亮/暗主题各过一遍视觉（M19）
- [ ] 断网/`getGoal` 失败时 Drawer 不白屏（走 07 §3）
