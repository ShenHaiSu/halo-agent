# 06 联调与验收测试计划

## 6.1 合闸命令（后端绿 + 前端绿方可合并）

```bat
:: 后端
dotnet build HaloAgent.sln -c Release
dotnet test tests/ -c Release
:: 前端
cd src\Web.Client && pnpm exec vue-tsc -b && pnpm build
```

新增单测最低集：`formatSmartBytes` 边界（0/1023/1024/1MB/5MB/NaN）+ `hitRate` 口径（input=0→0、钳制）+ 预设换算（本周周一起、跨月补零）+ `INSERT OR IGNORE` 去重（同 request_id 两次只留一）。

## 6.2 手工验收矩阵

### A. 流式字节（需求 1）
| # | 操作 | 期望 |
|---|---|---|
| A1 | 让模型写 ~50KB 文件 | 计数行从 `xx Bytes` 跳到 `x.xx KB = xxx Bytes`，150ms 内首跳，无卡顿 |
| A2 | 写含大量中文的文件 | 数字与完成态文件字节同量级（差 JSON 转义内），hover 有口径说明 |
| A3 | 流式中取消 | 数字冻结 + “已取消”，不归零不报错 |
| A4 | 小文件（<1KB） | 全程 `xx Bytes`，不闪出 KB |

### B. 用量库与 Tab（需求 2）
| # | 操作 | 期望 |
|---|---|---|
| B1 | 一轮多 tool call 对话结束 | `usage.db` 新增 N 行（N=本轮 HTTP 请求数），同轮时间三列一致；`llm-trace` 行数对照一致 |
| B2 | 切 8 个预设 + 自定义 | 瓦片/三图/明细同步变；`本周` 周一起算；`全部` 含历史 |
| B3 | Provider/Model 筛选 | 同名模型（如两家都有 `gpt-4o`）可分别点灭，堆叠柱系列独立 |
| B4 | hover 堆叠柱/折线/环形 | tooltip 含日期、各模型值、合计、命中率；图例点灭联动 |
| B5 | 空库（新装机） | 空态 `n-empty`，不白屏不报错 |
| B6 | 取消/失败轮次 | 有 usage 照记（status 标记），无 usage 不记，轮次不卡 |
| B7 | 数据根迁移 | `metrics/usage.db` 跟随迁移，旧库可查，新写进新库 |

## 6.3 性能基线

| 项 | 基线 | 测法 |
|---|---|---|
| 轮末 DB 写（10 行/事务） | ≤100ms 后台，不阻塞 TurnCompleted 返回 | 日志打点 `flushMs` |
| 万行聚合（trend/overview） | ≤100ms | `usage.getTrend` 计时 |
| 十万行聚合 | ≤500ms（超则加 `LIMIT` 天数或降精度） | 造数脚本（测试库，不进真库） |
| ECharts 30天×10模型 | 首绘 ≤500ms，hover 无掉帧 | Perf 面板 + `animation:false` 阈值验证 |
| Bridge 风暴 | 大文件写 Bridge 事件 ≤10/s | `ToolCallUpdated` 计数器 |
| 包体积 | ECharts 增量 ≤400KB（构建产物） | `vite build` 输出对比 |

## 6.4 `data-testid` 清单（自动化/录制用）

`tool-bytes-counter, usage-tab, usage-preset-today/yesterday/last3/last7/last30/thisWeek/thisMonth/all, usage-custom-range, usage-provider-select, usage-model-select, usage-query-btn, usage-tile-requests/input/output/cached/hitrate/models, usage-chart-stacked, usage-chart-hit, usage-chart-provider, usage-metric-switch, usage-table, usage-pagination`

## 6.5 Mock 约定（前后并行）

- `usageAnalyticsStore` 内 `USE_MOCK = true` 时返回固定 7 天 × 2 provider × 3 model 假数据（格式与 05 的 DTO 逐字段一致）；联调仅翻此开关 + 跑 B1–B7；Mock 代码随合闸删除或置 `false`。
- 回归红线：`chrome109` 构建无 `?.` 之外的新语法报错；暗黑/浅色各看一遍三图；Win7 实机（如有）开抽屉无白屏。
