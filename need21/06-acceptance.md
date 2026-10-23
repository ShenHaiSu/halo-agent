# need21-06 验收矩阵 + 性能预算 + 验证命令

> P1 验收功能正确性，P2/P3 验收性能。每条用例注明协议（resp/chat）与期望，没有“大概一致”。

---

## 1. 功能验收（P1 必须全绿，P2-P4 回归）

### 1.1 单轨正确性

| # | 用例 | 协议 | 期望 |
|---|---|---|---|
| F1 | 简单问答一轮（无工具） | resp + chat | 正文只出现一次；无 pill 残留（轮末 pill 消失）；复制 == 终态正文 |
| F2 | 多 iteration 工具轮（≥3 工具） | resp + chat | 每轮正文/推理/工具按到达序出现一次；工具组按 `assistantResponseId` 分段线正确；`+N` 收纳正确（>3 工具名截断） |
| F3 | 推理模型 | chat（deepseek 类） | 推理进 `ReasoningBlock`（默认收起）且只出现一次；流式期 pill 显示 `phase=reasoning`，无正文闪烁 |
| F4 | 失败工具 | 双协议 | 卡片 `有失败` badge + 自动展开 + `只看失败`；`headerSig` 翻转 ≤3 次 |
| F5 | 取消/中断/超长截断 | 双协议 | `TurnCancelled/TurnTruncated/TurnError` notice 正确；取消路径部分文本落袋可见（`AgentOrchestrator` L606-614 行为不变） |
| F6 | 含图消息 | 双协议 | `msg-images` 缩略图 + 原图链可用；丢失图 `部分图片已丢失` 提示 |
| F7 | 25 轮上限 | 双协议 | `已达到单轮最大迭代…发送“继续”` notice；`finishTurn` 清 pill |

### 1.2 交互一致性

| # | 用例 | 期望 |
|---|---|---|
| I1 | 右键复制本段/整轮 | 与重构前逐字一致（`mergeTurnTextFromBlocks` 口径；P1 用旧 `turnMergedTextOf` 输出做 diff，空行/截断标记一致即过） |
| I2 | 重放/分支/删除 | 行为不变；删除缺工具回包自动补中断占位（现有语义） |
| I3 | 折叠态 | reasoning 收起、toolGroup 收起、exec/fileEdit 展开、有失败展开——与重构前默认一致 |
| I4 | 终止按钮 | pill 内 `终止` 与旧 active 容器 `终止进程` 等效（同 `cancelAgentTurn(sessionId)`） |
| I5 | need19 跳转 | 目录点击/气泡/快捷键落点精确（`nav` 锚点未动，懒挂载占位块带锚点） |

## 2. 性能预算（P2/P3）

测试基：200 轮会话 + 3k 字回答 + 3 工具，Chrome Performance 录制流式全程。

| 指标 | 基线（P0 记录） | 目标 | 测量 |
|---|---|---|---|
| 单帧 `onFrameCoalesced+buildTurnUnits` | 填入 | <5ms（p95） | `__perf.timeline.maxMs` |
| 流式 `MarkdownStream` 单次耗时 | 填入 | 不进入 markdown（纯文本档 <0.5ms） | `__perf.markdown.lastRenderMs` 应 ≈0（流式期零调用） |
| 长任务（>50ms / >200ms） | 填入 | >200ms 为 0；>50ms ≤2 | Performance Long tasks |
| 单卡 header 翻转 | 填入 | ≤3 次/卡/轮 | `__perf.toolHeader` |
| 滚动跟随帧率 | 填入 | 贴底时 ≥50fps；非贴底零滚动 | FPS meter |
| 内存（200 轮常驻 DOM 节点） | 填入 | 屏外 turn 已卸载，节点数 ≈ 视口内 turn×块数 | Performance Monitor DOM Nodes |

## 3. 验证命令（`develop/src/Web.Client`）

```bash
npx vue-tsc -b
npx vitest run src/stores/__tests__ src/utils/__tests__
npx vite build
```

```bash
# 死代码零残留（P4 门禁，测试与注释除外）
grep -rn "useLegacyTurnGroups\|turnMergedTextOf\|syncMirror\|streamingText\|liveCallIds" src --include=*.vue --include=*.ts | grep -v __tests__ | grep -v "^\s*//" | grep -v "^\s*\*"
```

C#（仅当做了 04 §5 后端节流）：`dotnet build HaloAgent.sln`（以 `global.json` SDK 6.0.428 为准）。
