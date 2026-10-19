# need15-04 · 验收测试计划、性能预算、回归与灰度回滚

> 本文档是 P0/P1/P2 的统一验收口径。每个 P 合入前必须满足对应章节 + 全回归绿。

---

## 1. 测试文件与命名冻结

| 文件 | 覆盖 | 用例数目标 |
|---|---|---|
| `tests/HaloAgent.Tests/Stage15P0SafetyTests.cs` | P0-1~P0-5 | 10 |
| `tests/HaloAgent.Tests/Stage15P1UsabilityTests.cs` | P1-1~P1-4 | 8 |
| `tests/HaloAgent.Tests/Stage15P2ArchitectureTests.cs` | P2-1~P2-4 | 8 |
| 存量 `Stage14EditFileToolTests.cs` | 回归（A1~A7 + Perf） | 10（P0-3 需补 `limit` 参数，行为断言不变） |
| 存量 `Stage06PromptTests.cs` | P1-1 模板变更 | 1 处期望串更新 |

单测通用脚手架（复用 Stage14 风格）：`TempWorkspace`（`Path.GetTempPath()/halo15-<guid>`）+ `ToolContext{WorkspaceRoot:tmp, DataRoot:tmp/data, WorkspaceId:"ws", SessionId:"sess"}` + 直接 `new EditFileTool().ExecuteAsync(dto, ctx)`，不走网关。

---

## 2. 验收矩阵（逐项勾选）

### P0（阻塞项，一项不过不得合入）

| ID | 断言 | 文件不变性 |
|---|---|---|
| P0_1a | regex 含 `$` 按字面写回 | 仅目标区变化 |
| P0_1b | `regex_expand_groups:true` 时 `$1` 生效 | 同上 |
| P0_2a | 覆盖缺 flag → `NEED_CONFIRM_OVERWRITE` | 字节不变 |
| P0_2b | `CONTENT_TOO_LARGE`（300KB） | 字节不变 |
| P0_2c | hash 错 → `EXPECTED_HASH_MISMATCH`；对 → 成功 + `.bak` 与旧字节一致 | — |
| P0_2d | 新建文件无 flag 一次成功 | — |
| P0_3a | 缺 limit → `MISSING_LIMIT` | 字节不变 |
| P0_4a | 5 处同锚点 → `ANCHOR_AMBIGUOUS` 含 3 行号 | 字节不变 |
| P0_4b | 25 处 + replace_all → 成功 + `WARN_BULK_EDITS` | — |
| P0_5a | 全部新码 `ErrorCode` 非空 + `Error.StartsWith(CODE)` | — |

### P1

| ID | 断言 |
|---|---|
| P1_1 | `BuildAsync` 含三硬规则串 |
| P1_2a | Schema 无 `expand_tabs` 但老参数仍解析 |
| P1_2b | `old_string:"x"` → `ANCHOR_TOO_SHORT` |
| P1_3a | edit_file 成功含 64hex hash 且与落盘 sha256 一致 |
| P1_3b | read hash → edit_lines(expected_hash) 联动通过 |
| P1_3c | 三工具成功 `FileBytes>0` |
| P1_4 | 空文件 `offset=1,limit=0` 插入成功；孤立 `\r` 越界文案含双口径行数 |

### P2

| ID | 断言 |
|---|---|
| P2_1a | dry_run 三工具均不改 mtime/bytes，但含 `DRY_RUN` + `@@` |
| P2_1b | dry_run + 错 hash 仍报 `EXPECTED_HASH_MISMATCH` |
| P2_2a | 三次写后 3 bak + 3 meta；undo 恢复到第二次 |
| P2_2b | 55 备份 → prune 到 50 |
| P2_2c | backup 越权路径拒绝 |
| P2_3 | 三类码回包自动带 `NextAction` 草稿 |

---

## 3. 性能预算（继承 need14，不放宽）

| 场景 | 预算 | 位置 |
|---|---|---|
| 1MB 文件 `normalized` 命中 | <200ms | 存量 `Perf_NormalizedMatch` 仍绿 |
| 3000 行 `nearest` 搜索 | <500ms | 存量 Perf 仍绿 |
| `read_file` hash header（≤1MB） | <10ms 额外 | P1_3 单测计时断言 |
| 备份 `File.Copy`（≤256KB） | <50ms | 不计入匹配预算，P0_2c 计时打印即可 |

---

## 4. 回归清单（每次合入必跑）

```cmd
dotnet test tests/HaloAgent.Tests/HaloAgent.Tests.csproj -c Debug
```

* Stage01~Stage14 全绿；仅允许的两处期望变更：① Stage14 A6/A6b 补 `limit`；② Stage06 模板期望串更新（diff 贴 PR）。
* 双协议 Schema 快照：`GetChatToolsJson/GetResponsesToolsJson` 经 `JsonDocument.Parse` 合法。
* 前端：`build-dist.cmd` 不必每次跑；但 `ToolRegistry.ToolNames` 数量变化（P2-4 若加 `edit/undo_edit` 11→12/13）需同步改 `Stage05ToolExecutionTests` 数量断言。

---

## 5. 灰度与回滚

1. **顺序**：P0 灰度 7 天（观察 `.bak` 产生率、`NEED_CONFIRM` 触发率、污染事故 0）→ P1 灰度 3 天（抽 50 任务看一次成功率）→ P2 灰度 3 天。
2. **开关**：P0-2 的 `DefaultMaxWriteBytes`、P0-4 的 `MaxBulkWarnThreshold=20`、P2-2 的 `keep:50` 全部做成 `internal static` 可调字段（单测可改，线上无需重启即可热调——参考 `AskUserGate.DefaultTimeoutSec` 模式）。
3. **回滚**：
   * 参数级：`edit_file(match_mode:"exact")` 回严格语义；`write_file(max_bytes:1000000)` 临时放宽上限。
   * 代码级：按 `00-README §5` 纪律，每 P 独立 revert；备份目录只增不删，回滚不丢恢复能力。
4. **观测**：复用 `HaloLogger.Tool.Info` 日志行，新增码直接可 `grep ANCHOR_AMBIGUOUS|NEED_CONFIRM_OVERWRITE|MISSING_LIMIT` 统计触发率；建议在 `UsageMetricsService` 加三个计数器（可选，不阻塞合入）。
