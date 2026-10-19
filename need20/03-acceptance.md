# need20-03 验收矩阵（F1/F2 必过 + 回归红线）

> 对应总览：[00-README](./00-README.md) §4；施工见 [02](./02-fix-plan.md)。
> 约定：Drawer 关闭 = 主界面；Drawer 打开 = 右侧栏。全部手工验收。

---

## 1. 缺陷验收（必过，不通过不合入）

| # | 关联 | 场景 | 操作 | 通过标准 |
|---|---|---|---|---|
| F1-1 | F1 | Drawer 关闭 | 观察主界面右下角 | 无任何浮动卡/浮动按钮；`document.querySelector('[data-testid="goal-float-panel"]') === null` |
| F1-2 | F1 | 全仓搜索 | `findstr /n "GoalFloatPanel" src\ -r` | 零命中（除文档） |
| F1-3 | F1 | 打开 Drawer → goal tab | 观察内容区 | 目标信息/编辑/重置完整可用；与删除前浮卡数据一致（同源 `goalStore.goal`） |
| F2-1 | F2 | 点触发器 | 观察抽屉 | 从右侧滑入，宽 360（可拖 320~720），左侧 48px 纵栏可见 |
| F2-2 | F2 | 打开后看 rail | 数 icon | 恰 2 个正方形按钮（🎯 目标 / ☰ 历史目录），36×36，纵向排列，hover 有 tooltip |
| F2-3 | F2 | 默认 tab | 观察内容区 | 默认 `toc`：历史气泡目录（user 右 / assistant 左），非空白 |
| F2-4 | F2 | 切到 goal tab | 点击 🎯 | 内容区切换为 Goal 面板；rail 高亮条切到 🎯 |
| F2-5 | F2 | 点遮罩 20 次 | 点击抽屉外部 | 始终打开，无闪烁（R6 保持） |
| F2-6 | F2 | 点右上角 X | 点击 X | 收起；再次点触发器状态（tab/宽度）保持（R7 保持） |
| F2-7 | F2 | 拖左边缘 | 拖拽宽度 | 320~720 夹紧；重启后保持（localStorage） |
| F2-8 | F2 | Esc | 按 Esc | 关闭（Q1 保持默认允许） |

---

## 2. 回归红线（沿用 need19-06 §3，抽查核心）

| # | 红线 | 验证方法 |
|---|---|---|
| R-01 | 主界面像素（Drawer 关闭） | 与删除浮卡前对比：允许右下角浮卡消失，其余（时间轴/输入舱/状态栏）一致 |
| R-02 | 自动滚到底不变 | 打开会话自动到底；发消息跟随到底 |
| R-04 | 既有 `data-testid` | `message-timeline / prompt-composer / composer-send / msg-images / toc-* / rail-tab-* / goal-panel-content / goal-save-btn / goal-reset-btn / goal-progress-bar / goal-milestone-item` 存在且可点；`goal-float-panel / goal-mini-btn / goal-expand-btn / goal-collapse-btn / goal-minimize-btn` 应**不存在**（已删除） |
| R-05 | 右键菜单/重放/分支/删除 | Drawer 关闭时逐一操作正常 |
| R-07 | 子会话只读视图 | 进入子会话正常；目录标题变为"子会话目录（只读）"，Goal 为父目标只读 |
| R-09 | 设置中心 | 顶栏设置可开，可与右侧栏共存 |
| R-10 | Console 干净 | 全程无新增 error/warn |

---

## 3. 门禁命令

```cmd
cd develop\src\Web.Client
call pnpm exec vue-tsc -b
call pnpm build
node --experimental-strip-types scripts/toc.selfcheck.mjs
```

- 合入条件：三条全绿 + §1 全过 + §2 抽查全过。
- 产物：`develop/src/Host.Desktop/wwwroot/assets/index-*.js` 更新，主 chunk 增量应 < 40KB（gzip 前；本次为删除代码，预期不增反减）。

---

## 4. 边界抽查（need19-07 子集，F2 修后重点）

| # | 边界 | 方法 | 期望 |
|---|---|---|---|
| X1 | 空会话 | 新建会话开面板 | 空态插图 + 文案，不空白 |
| X4 | 1024×700 窄窗 | 缩窗口开抽屉 | `max-width:min(720px,86vw)` 生效，rail 不被挤压 |
| X10 | 双 Drawer | 右侧栏 + 设置中心 | 都能开关，层级正确 |
| X11 | 亮/暗主题 | 各开一遍 | 配色正常，无白底白字 |
| X12 | Win7/Cef109 | 抽测 | 启动/开栏/跳转可用，无 `:has()` 报错 |
