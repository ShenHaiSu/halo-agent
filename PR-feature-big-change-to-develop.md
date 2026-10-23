# PR: `feature/big-change` → `develop`

> **标题建议**：`feat(big-change): 网络装配线 / 上下文预算与压缩 / 长会话渲染重构（P0–P2 落地）`
> **类型**：破坏性重构（Breaking Change），按 `docs/bigChange/00-overview.md` 设计总纲执行
> **仓库**：https://github.com/ShenHaiSu/halo-agent
> **源分支**：`feature/big-change` @ `ca0e4de`
> **目标分支**：`develop` @ `da92382`（merge-base：`da92382`，`develop` 无独有提交领先，可线性合并）
> **提交数**：6 个（全部未推送合并）
> **变更规模**：82 个文件，`+5927 / -258`
> **本文档位置**：`docs/PR-feature-big-change-to-develop.md`（基于 `git` 静态差异分析生成，未执行构建/测试）

---

## 1. 变更概览

本次 PR 把大改造 `bigChange` 的 **P0（网络）、P1（上下文）、P2（渲染/交互）** 三个阶段一次性带入 `develop`，并附带三套设计文档。整体思路：

1. **网络**：单装配线、顺序不可换、观测先于重试（`capture → classify → retry`）。
2. **上下文**：缓存命中 = 字节级稳定（固定排序 + 增量补丁 + 易变后置 + usage-anchor），预算全部由 window 推导，压缩改为 token 切点。
3. **渲染**：历史冻结、尾部独活（history/tail 分离 + 引用保稳 + rAF 合批 + 块级 memo）。

### 提交清单

| # | Commit | 主题 | 变更 | 文档阶段 |
|---|---|---|---|---|
| 1 | `08757d2` | `chore: bring uiuxAndStyle/contextAndRender/bigChange docs to feature/big-change` | 29 files, +1693 | 参考文档（第一/二/三参考） |
| 2 | `4a6562a` | `refactor(server): extract gateway resilience pipeline with split retry pools` | 8 files, +638/-43 | P0-1 / P0-2 |
| 3 | `cea77df` | `feat(server): derive context budget from window with calibration and compaction` | 11 files, +1445/-12 | P1-1 / P1-2 / P1-3 |
| 4 | `9abb93e` | `perf(web): batch streaming writes via rAF frameBatcher and block markdown` | 20 files, +1181/-45 | P2-1 / P2-2 |
| 5 | `e10ebdf` | `feat(web): virtualize transcript with history-tail split and follow mode` | 8 files, +680/-149 | P2-3 |
| 6 | `ca0e4de` | `feat(web): add turn phase header and composer send gates` | 6 files, +290/-9 | P2-6 / P2-8 |

> 说明：P2-5（搜索/disclosure）、P2-7（markdown 门控）的工具类已随 `9abb93e` 落地（`disclosure.ts` / `markdownBlocks.ts` / `renderContentLimits.ts`）；`10-minimap-smooth-settle` 已在 `e10ebdf`/`9abb93e` 中合入（`conversation-minimap.ts` / `transcriptSettle.ts` / `useSmoothText.ts`）。**`P3`（`08-cutover-testing.md` 的全量验收矩阵与迁移）不在本 PR 声明完成的范围内**，见第 6 节风险项。

---

## 2. P0 网络层：装配线与分池重试

**相关文件**
- 新增：`src/Host.Desktop/Infrastructure/Network/LlmHeaderPipeline.cs`、`LlmGateway/ProviderRetryPolicy.cs`、`ProviderResponseCapture.cs`、`ProviderTransportHealth.cs`、`StreamIdleWatchdog.cs`
- 修改：`LlmGateway/LlmGatewayClient.Resilience.cs`（+125/-38）、`LlmGateway/LlmGatewayClient.cs`（+15/-3）

**做了什么**
- `SendRequestAsync` → **`SendThroughPipelineAsync`**（破坏性重命名）。管道内顺序固定：`Serialize → Normalize → BuildRequest → Capture → Retry(分池) → IdleWatchdog → Parse`；管道内只有一个 `new HttpRequestMessage` 点（`SendOnceThroughPipelineAsync`）。
- **鉴权与业务头分离**：`Authorization` 为唯一鉴权点，先清 `api-key` / `x-api-key` / `Authorization` 再注入；业务头经 `LlmHeaderPipeline.NormalizeCustomHeaders` 清洗（禁键丢弃 + Warn）后 `ApplyHeaders`，支持 `${session-hash}` 等变量解析；`NormalizeBaseUrl` 统一补齐 path。
- **重试分池**（`ProviderRetryPolicy`）：429 池 `RateLimitedMax=10`、`RateInitialMs=2000` 指数退避、`RateCapMs=30000`，并尊重 `Retry-After`；瞬时池（5xx / 408 / 409 / 传输异常）`TransientMax=10`、`TransientCapMs=8000`；`Terminal` 类错误（4xx 语义错）**永不重试**。
- **`400/422` 一次性自愈**：命中「不支持 max_tokens」类报文时 strip 后重放一次（不消耗重试预算）。
- **传输健康 + 重建**：`ProviderTransportHealth` 按 origin 统计失败/成功，达到阈值触发传输重建（`RecreateTransport`）。
- **空闲看门狗**：`StreamIdleWatchdog`，零事件 180s（`HALO_STREAM_IDLE_TIMEOUT_MS` 可配，下限 30s，`0` = 关闭）abort + 排空，防双计费。
- 本方法只负责**起流前**重试；`sawStart` 之后不重放（由调用方透传）。

**行为变化（用户可见）**：弱网/429 场景 turn 可能变长但可以恢复；旧「1s/2s/4s 各 3 次」语义被删除。

---

## 3. P1 上下文层：window 预算 + 校准 + token 切点压缩

**相关文件**
- 新增：`Services/ContextBudgetLimits.cs`、`ContextCalibration.cs`、`OutputCap.cs`、`CompactionPlan.cs`、`CompactionSummaryInput.cs`、`SystemTranscript.cs`、`SystemTranscriptJournal.cs`、`TranscriptCompat.cs`
- 修改：`Services/TokenBudgetService.cs`（+12/-7）、`Services/TokenBudgetService.Cuts.cs`（+5/-3）

**做了什么**
- **预算公式全部从 window 推导**（`ContextBudgetLimits`，父子同公式，无魔法阈值）：
  ```
  window        = model.MaxContextTokens ?? 128000
  outputBudget  = min(model.MaxOutputTokens ?? 8192, window*0.25)
  reserveFloor  = min(16384, window*0.5)
  headroom      = min(window-1, max(reserveFloor, outputBudget, window*0.05))
  hardLimit     = window - headroom
  keepRecent    = clamp(hardLimit*0.2, 8000, 64000) 且 ≤ hardLimit*0.5
  autoThreshold = hardLimit*0.9
  retainedUser  ≤ min(20000, hardLimit*0.5)
  ```
  示例：8192 窗 → headroom = **4096**（旧固定 `DefaultSafetyBuffer = 1024` 已删除）。
- **估算系数统一**：ASCII 由 `3.6` → `3.5` chars/token（`CharsPerToken`）；单张存活图片改为 `ImageCharsPerImage = 4800` 字符折算（`ImageTokensPerImage` 默认 ≈ 1372，可配）；被裁剪占位符只按文本计费。
- **校准（`ContextCalibration`）**：双序列（有锚 `trailingRatio` / 无锚 `unanchored`），窗口 16 中位数；不对称收敛——上行即时，下行需 3 连击且每步 ≤15%，冻结 2 连 misreport，硬地板 `0.85×raw` / 顶 `6×`，`[0.5,3]` 外丢弃。**校准只影响估算，不改账单真值**。
- **`OutputCap`**：输出上限输入 = system prompt + tool schemas 投影 + hosted-search 投影。
- **压缩（`CompactionPlan` / `CompactionSummaryInput`）**：从尾按 token 累积到 `keepRecent` 落合法切点（不断工具链）；孤儿 `toolCall` 剪除；单消息截断；`retained` 只保留 active 最新 user；摘要分片 ≤16 块；`toolResult` 降级截断 500 字符 + `…[truncated]`；尾已 checkpoint 时跳过重复压缩。
- **System 稳定前缀（`SystemTranscript` / `Journal` / `TranscriptCompat`）**：section 按写入顺序 `Join("\n\n")`、空 section 整块丢弃；只换 runtime 保留 `tool_activation`（timestamp 单调 +1）；会话级 Sync 按 `sessionId` 持有上轮快照并返回「是否变化」；journal 水位瞬间削掉整块再进摘要输入。

---

## 4. P2 渲染/交互层

### 4.1 流式合批 + 块级 Markdown（`9abb93e`）
- 新增共享 `shared/utils/frameBatcher.ts` 与会话级 `session/utils/frameBatcher.ts`：流式写入按 rAF 合批，`terminal` 事件 `flushNow()` 强制落盘，消除 per-chunk Pinia churn。
- `markdownBlocks.ts` + `MarkdownBlock.vue`：块级 memo，**已定稿块跳过重渲染**；`renderContentLimits.ts` + `markdownRender.ts` 负责超限降级（超大 fence → 纯文本兜底）。
- `codeHighlight.ts`（懒加载高亮）、`MermaidBlock.vue`（失败隔离）、`disclosure.ts`、`transcriptCache.ts` / `transcriptProjection.ts` / `transcriptWindow.ts` / `transcriptSettle.ts`。
- `TimelineRow.vue` / `TranscriptHistory.vue` / `TranscriptTail.vue` 拆出，`useSmoothText.ts` + `smooth-cursor.css` 提供平滑打字光标。

### 4.2 长会话虚拟化 + follow 模式（`e10ebdf`）
- `TimelineView.vue` 重构为 **history / tail 分离**（冻结引用、窗口化挂载），`readingWindow.ts` 判定阅读窗，`transcriptScroll.ts` 负责滚动位置保持。
- follow 状态：`pinned` / `showJump`；隐藏 pane 永不 follow、不采样；上翻到顶先扩窗再加载更早历史。
- `ConversationMinimap.vue` + `conversation-minimap.ts`：迷你地图定位。

### 4.3 Turn 四态 + Composer 门控（`ca0e4de`）
- `turnProcess.ts` 提供 `turnPhaseOf` / `runningCopy` / `duration`；`TurnGroupItem.vue` 增加阶段头；`TurnOutcomeCard.vue` 只在失败且不在阅读窗时出现。
- `ActionDock.vue`：permission / ask / plan 三类卡片。
- `composerGates.ts` + `useComposer.ts`：Enter 发送、**IME 合成期不发送**、`Alt` 转向（Alt-steer）、Esc 停止 run；Bash 退出码优先于调用状态。

---

## 5. 破坏性变更清单（`develop` 合入前必读）

| 旧语义 | 新语义 | 用户可见影响 |
|---|---|---|
| `SendRequestAsync` + 固定 1s/2s/4s、最多 3 次重试 | `SendThroughPipelineAsync` + 分池 10 次 + `Retry-After` + 看门狗 | 弱网 turn 变长但可恢复 |
| `TokenBudgetService.DefaultSafetyBuffer = 1024` | `ContextBudgetLimits.Headroom`（8192 窗 → 4096） | 水位一次性跳变 |
| `CompactionService` 按 group 数切分 | `CompactionPlan` token 切点 + 孤儿剪除 | 压缩后（更长窗口）保留更多上下文 |
| ASCII `3.6` chars/token；图片 1000 tokens | `3.5` chars/token；图片 4800 字符折算 | 估算值小幅变化 |
| `markdownRender` 整源渲染 + `blockHash` 整源 key | 块级 memo + tail-only + 超限降级 | 首屏高亮可能闪一次 |
| 前端 `timelineItems` 全量重算 + 无节流写入 | history/tail + rAF 合批 | 切会话无白闪、长会话不掉帧 |
| 自定义 Header 禁键可写 | 禁键清洗后丢弃 + Warn | 违规头被丢弃 |

**已删除的符号/常量**：`DefaultSafetyBuffer`、`SendRequestAsync`、旧 `3.6` 系数、旧整源 blockHash 缓存路径。旧 session 压缩中间格式不保留兼容层。

---

## 6. 测试与验证

### 已更新用例
- `tests/HaloAgent.Tests/Stage04WorkspaceAndBudgetTests.cs`：`SafetyBuffer` 1024 → 4096，`AvailableInputTokens` 期望值同步改为 `8192 - 2048 - 4096 - 150`。
- `tests/HaloAgent.Tests/Stage22P1StabilityTests.cs`：429 handler 由 10 次样本 → 20 次样本，期望 `CallCount` 4 → **11**（对应新 429 池：初始 1 次 + 最多 10 次重试）。

### 建议的合并前验收（摘自 `docs/bigChange/08-cutover-testing.md`）
```bat
dotnet build HaloAgent.sln
pnpm -C src/Web.Client typecheck
pnpm -C src/Web.Client lint
pnpm -C src/Web.Client test
pnpm -C src/Web.Client build
```
- [ ] 残留旧语义全局搜索（预期只剩 `08` 迁移提示）：`SendRequestAsync(` / `DefaultSafetyBuffer` / `KeepDefaultGroups` / `SEG_FLUSH_MS` / `MD_CACHE_MAX`
- [ ] **网络**：429（带 `Retry-After`）/ 5xx / 超时 / 代理断 → 可恢复、无双计费、无 terminal 误判
- [ ] **上下文**：同会话连发 3 轮 system stableHash 不变；加压到 90% 线触发内联压缩；压缩后续聊工具链不断
- [ ] **渲染**：200+ 轮会话流式 + 上翻 + 切会话 + 搜索定位，全程无掉帧、无抢滚、无白闪；46KB fence 与超限源降级正确
- [ ] **账单**：usage 有值时以真值为准，校准不改账单；取消/错误同样落袋但不重算
- [ ] **安全**：禁键头不可发送；mermaid/KaTeX 失败隔离；外链 `noopener`；本地图越界回 chip
- [ ] 兼容铁律：`chrome109` build target、`net6.0-windows`、CefSharp `109.1.110` 未被改动

### 验证状态（**Reviewer 请注意**）
本次 PR 文档基于 `git` 静态差异分析生成；**构建/单测/前端 typecheck 未在本环境内实际执行**，请 Review 时以 CI 结果为准。

---

## 7. 已知风险与遗留

| 级别 | 事项 | 建议 |
|---|---|---|
| 高 | `P3` 全量验收矩阵（`08-cutover-testing.md`）未声明完成；无回滚 tag | 合并前打 `pre-bigchange` tag；出口标准为「整包回滚到重构前 tag」 |
| 高 | `develop` 之后若有会话数据，旧压缩中间格式不再兼容 | 首次打开旧会话按 `03` 重建 sections，前缀会变化一次（可接受） |
| 中 | `ProviderRetryPolicy` 单次上限 10 次 ×30s 退避，极端弱网下单 turn 可能 >5 分钟 | 建议在 UI 暴露「当前重试中」状态（`ca0e4de` 已有阶段头可复用） |
| 中 | `ContextCalibration` 初次进入时无锚样本，估算偏差较大 | 可用 `usage` 锚点前 3 轮观察日志 `[Context] calibrat*` |
| 中 | `docs/bigChange` / `contextAndRender` / `uiuxAndStyle` 三套文档一次性 +1693 行 | 建议 PR 描述里指明 `00-overview.md` 为唯一入口 |
| 低 | `RecreateTransport` 当前只打 Warn 日志（重建挂点注释指向 `HaloHttpClientFactory.Recreate`） | 后续 P0 收尾时补真实重建 |
| 低 | `useSmoothText` / `transcriptSettle` 的阈值依赖 `requestAnimationFrame` | 低端 Win7 机器需实测帧率 |

---

## 8. 合并方式与 Checklist

- 分支关系：`merge-base = origin/develop 顶端`（`0 6` 计数），**无冲突，可 squash 或 merge commit 任选**；建议 **merge commit** 保留 6 个阶段化提交，便于按阶段回滚排查。
- 建议 PR 标签：`breaking-change`、`area:server`、`area:web`、`perf`、`docs`

```bat
:: 合并前
git fetch origin
git checkout feature/big-change
git log --oneline origin/develop..HEAD      :: 应为 6 条
dotnet build HaloAgent.sln
pnpm -C src/Web.Client typecheck && pnpm -C src/Web.Client lint && pnpm -C src/Web.Client test && pnpm -C src/Web.Client build

:: 合并后
git checkout develop
git merge --no-ff feature/big-change
git tag pre-bigchange-rollback develop~1    :: 回滚锚点
git push origin develop --tags
```

- [ ] 6 个 commit 全部保留（含 docs 提交）
- [ ] 上述构建 + 前端四件套全绿
- [ ] 破坏性清单（第 5 节）已逐条确认
- [ ] 回滚 tag 已打
- [ ] `README` 补充重构说明 + `docs/pitfalls/` 新增一条重构记录（`08-cutover-testing.md` 出口要求）

---

## 9. 文档索引（本 PR 附带）

| 文档 | 用途 |
|---|---|
| `docs/bigChange/00-overview.md` | **第一参考**：重构路线图 + 破坏性总清单 + 全局验收 |
| `docs/bigChange/01`–`07`、`09`–`14` | 各阶段实现口径（管道/重试/稳定前缀/预算/压缩/渲染/markdown/滚动/minimap/搜索/turn 卡/门控/composer） |
| `docs/bigChange/08-cutover-testing.md` | **P3 破坏性切换 + 迁移 + 全量验收矩阵** |
| `docs/contextAndRender/00`–`06` | 第二参考：优秀方案思想来源（栈不同，不可直搬） |
| `docs/uiuxAndStyle/01`–`07` | 第三参考：行为/阈值/文案口径 |

对照关系（引自 `00-overview.md`）：`01→context01`、`02→context02`、`03→context03`、`04/05→context04`、`06→context05`、`07/13→context06`、`09→uiux01`、`10→uiux02+03`、`11→uiux04`、`12→uiux05`、`13→uiux06`、`14→uiux07`。
