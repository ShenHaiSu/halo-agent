# 07 联调测试计划（G/H/S 矩阵 × 合闸命令 × 回滚）

> 跑通本篇方可合闸。命令一律 `cmd.exe`（`runner:cmd`）；复杂断言用 `runner:python` 读 JSONL/JSON。

## 1. 后端合闸

```cmd
cd /d F:\MyProgram\CSharp\halo-agent
dotnet build HaloAgent.sln -c Release
dotnet test tests\HaloAgent.Tests -c Release
```

- 新增单测命名：`Stage16Goal*`（02 §8 六条）、`Stage16SubDetail*`（05 §7 五条）；老用例零修改全绿。
- 失败即停：先修后端再动前端（与 need13 Step E/F 顺序一致）。

## 2. 前端合闸

```cmd
cd /d F:\MyProgram\CSharp\halo-agent\src\Web.Client
npx vue-tsc -b
npx vite build
```

- `data-testid` 全量核对：03 §6 + 04 §7 + 06 §5（`grep -r data-testid src`  diff 清单）。
- 暗色/亮色各截三图：浮动窗三态、子历史、诊断三区。

## 3. G 矩阵（Goal，12 条）

| # | 操作 | 预期 |
|---|---|---|
| G1 | 主会话首轮 `goal_tool set{title,desc,milestones[3],percent:10}` | `goal.json` 落盘；浮动窗 collapsed 显示 10%；下轮 system prompt 含 Goal 块（`previewPrompt` 验证） |
| G2 | `progress{percent:40}` | 进度条 40%；事件 `goal:updated` 前端闪更新 |
| G3 | `complete_milestone{title:其一}` | 里程碑勾选 + percent 自动重算 33→见公式 |
| G4 | `progress{percent:150}` | 钳制 100 |
| G5 | 子内 `goal_tool get` | 返回父 Goal 全文 |
| G6 | 子内 `goal_tool set` | `[GOAL_DENIED]` 回 LLM；文件 mtime 不变 |
| G7 | 浮动窗人工改标题保存 | `UpdatedBy=user`；下一轮生效横幅 |
| G8 | 浮动窗重置（二次确认） | 空态 + mini 引导 |
| G9 | 手工改坏 `goal.json`（写半截） | 前端 corrupt 横幅 + 可重置；`get` 空态 |
| G10 | 删主会话 | 目录含 `goal.json` 一并消失 |
| G11 | mini/collapsed/expanded 切换 + 拖拽 + 记忆刷新 | 态记忆恢复；位置记忆恢复 |
| G12 | 流式中 Goal 更新 | 浮动窗数字闪更新，时间轴不跳动 |

## 4. H 矩阵（子历史，10 条）

| # | 操作 | 预期 |
|---|---|---|
| H1 | 主 spawn 2 子完成后点工具卡 `查看子会话 →` | 面包屑三段；子正文与主时间轴同渲染；返回主零重拉 |
| H2 | running 子进历史 | 进度条 + 3s 轮询；切回主即停 |
| H3 | 运行表行内 `历史` 进入 | 同 H1 |
| H4 | barrier 条 `查看子进度` 进入 | 同 H1 |
| H5 | 子历史右键 | 无菜单（只读提示） |
| H6 | 子历史输入舱 | 只读 alert，无发送按钮 |
| H7 | failed 子进入 | 状态徽标 + 失败原因行 |
| H8 | 主 id 调详情接口 | `NOT_A_SUB` |
| H9 | 对子调截断/分支/删除 | `SUB_READONLY` 且消息数不变 |
| H10 | 8 子下拉切换 | 滚动正常；running 置顶 |

## 5. S 矩阵（诊断，8 条）

| # | 操作 | 预期 |
|---|---|---|
| S1 | 打开 agent Tab | 三区 + sticky bar；老 `data-testid` 全在 |
| S2 | 改超时保存 | 脏态→保存→下一轮生效；`fallbackReason` 横幅位正常 |
| S3 | 开 memory 写（危险） | 红色描边 + tooltip；保存后子写 memory 放行 |
| S4 | 拒绝记录 | 子写拒/超限拒出现在记录卡（含 code/callId/时间） |
| S5 | 运行表自动刷新 | 5s 刷新；切 Tab 即停 |
| S6 | Goal 摘要卡跳转 | 跳主界面 + 浮动窗展开 |
| S7 | 1020px/94vw 小窗 | 无横溢；表格内滚动 |
| S8 | 暗色切换 | 无硬编码白底；hover 一致 |

## 6. 双端一致

- `hostBridge.ts` mock 与真后端同字段（Goal/详情/RecentRejections），无后端直开前端三界面可渲染空态。
- 事件名统一：`goal:updated` 大小写全仓一致（前后端各一处常量注释）。

## 7. 回滚

1. 前端：`GoalFloatPanel/subSessionStore/SubHistoryView/agent cards` 全新文件，删文件 + 恢复四处挂载即回滚。
2. 后端：`GoalTool` 注册行注释 + `Goal.cs` partial 排除编译即回滚（`goal.json` 残留无害，老代码不读）。
3. 配置：`agent.json` 本期零字段新增，无需迁移回滚。
