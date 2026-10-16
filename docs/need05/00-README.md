# need05 施工方案总览（Token/上下文口径统一 · 稳定前缀提命中率 · /compact 落地）

> 输出路径：`docs/need05/`；只做方案，不改代码。
> 基线：`need04-05` 已落地时刻的代码（`TokenBudgetService.ResolveUsedInputTokens/ResolveCachedTokens/CalculateBudgetStatus`、`WorkspaceSessionService.AppendSessionMessageAsync` 快照语义、`AgentOrchestrator` 落盘 `Input/Output/CachedTokens`、`LlmGatewayClient` 双协议 `usage` 解析、`WorkbenchTopBar/MessageTimeline/agentStore/workspaceStore` 三端显示）。
> 参考输入：`E:\ProgramFiles\znaide`（`crates/core/src/llm/types.rs/openai.rs/session.rs`、`crates/core/src/config.rs`、`crates/tui/src/app.rs` 的 `TokenStats/est_tokens/ctx_usage_badge/compact_context`）。
> 与 `need04-05` 关系：04-05 把“总量被当增量累加”修成快照语义；本期把口径做完整（账单 vs 占用分离、未知态、`≈`估算），把前缀做稳定（提 `prompt cache` 命中率），并落地 `/compact`。

## 1. 三个目标与文档拆分

| # | 目标 | 方案文档 | 主要触点 |
|---|---|---|---|
| 1 | 正确显示和计算 token 用量和上下文用量 | `01-token-usage-correctness.md` | `UnifiedTokenUsage/UnifiedChatMessage/ContextBudgetStatusDto/SessionMetadata`、`TokenBudgetService`、`AgentOrchestrator`、`LlmGatewayClient`、`SessionRepository.Export`、`agentStore/workspaceStore`、`WorkbenchTopBar/MessageTimeline/hostBridge mock` |
| 2 | 正确显示和拼接上下文，保证请求头历史字节稳定，提高命中率 | `02-stable-context-prefix.md` | `SystemPromptBuilder`、`ToolRegistry`、`TokenBudgetService.BuildWithinBudget/TruncateToBudget`（新增高低水位块淘汰）、`AgentOrchestrator` 组装处、`LlmGatewayClient` 日志、`Chat/ResponsesAdapter.SerializeRequest` 顺序约束 |
| 3 | `/compact` 的具体实现 | `03-compact-impl.md` | 新增 `ICompactionService/CompactionService`、`HostBridgeFacade.Compact*`、`SessionRepository` 原子重写+备份、`AgentOrchestrator` 复用、`workspaceStore/agentStore` 事件与 `/compact` 命令 UI |
| — | 全链联调与验收（必读的施工顺序与合闸命令） | `04-integration-test-plan.md` | 上述全部 + `tests/HaloAgent.Tests` 新增用例 + `vue-tsc/vite/dotnet build` |

## 2. 全局施工原则（四篇通用，出现冲突以各篇“精确到行”为准）

1. **零破坏只增量**：不动已有 C# DTO 必填字段名与 JSON 小驼峰结构，只新增可选字段/事件 `payload` 键；不动 `messages.jsonl` 既有行格式；前端只用已验证的 `Naive-UI 2.45.3 + CefSharp Chrome 109` 能力。
2. **口径先行**：`IN=占用快照（末轮 prompt）`，`OUT=本轮生成`，`Σ=计费累计`，`cache⊆IN`，`未知=显式 unknown` 四句为全仓统一语言，注释与 UI 文案照此写。
3. **稳定优先于省**：任何“省一点 token 但抖动前缀”的优化一律不做；裁剪只在高水位触发、一次裁到低水位。
4. **失败不丢数**：压缩/重试/取消失败时保持原上下文与原统计不动；未知占用画 `?`，绝不画 `0%` 误导。
5. **可测性**：沿用 `data-testid`（`context-quota-meter-badge/message-timeline/active-turn-container`）；后端 `dotnet build + dotnet test`，前端 `vue-tsc -b && vite build`。

## 3. 强制施工顺序（先后端+单测，再前端，最后联调）

```text
Step A 后端改造（01 §3 → 02 §3 → 03 §3，按篇内文件顺序改）
  └─ 每篇改完即跑：dotnet build + dotnet test（过滤新增用例全绿）
Step B 前端改造（01 §4 → 02 §4 → 03 §4）
  └─ 每篇改完即跑：vue-tsc -b && vite build（含 mock 对齐）
Step C 联调测试（04 全文，3 轮对账 + 前缀哈希 + /compact 端到端）
```

禁止后端单测未全绿就开始前端改造；禁止跳过 `04` 的四处 `IN` 同源与前缀稳定性检查。

## 4. 验收总表（详情跳转各篇 §验收）

- [ ] 01：顶栏水位、本轮 footer、持久 footer、`meta.json` 四处 `IN` 同源（同一轮差值 <1% 或纯估算误差）；`cache≤IN` 恒成立；`Σ` 随调用次数单调涨、`占用` 趋稳不翻倍；未知态画 `?`；流式中 `≈` 估算、到账切真实。
- [ ] 02：同会话连续 3 轮请求前缀哈希仅在裁剪/压缩轮变化，其余轮 `system+历史头` 字节一致；`llm-trace` 可查 `prefixHash/in/cached/hitRate`；`system` 日期等易变行已移尾部；工具定义顺序稳定。
- [ ] 03：`/compact` 成功后 `system+摘要+尾部` 生效、`messages.jsonl.bak` 存在、累计账不清零、占用置未知待下轮校准；失败原样不动；压缩中不可取消且 UI 有明确忙态。
- [ ] 04：`dotnet build + dotnet test + vue-tsc -b + vite build` 全绿；`ExportSessionMarkdown` 的 `In/Out` 与新口径一致。
