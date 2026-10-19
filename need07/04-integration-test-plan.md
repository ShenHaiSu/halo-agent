# need07-04 联调与验收（单测 + 手工矩阵 + 合闸）

> 前置：`01/02/03`。本篇是合闸唯一依据，逐项打钩。

## 1. 后端合闸

```powershell
dotnet build HaloAgent.sln
dotnet test tests/HaloAgent.Tests/HaloAgent.Tests.csproj --filter "Stage07History"
dotnet test tests/HaloAgent.Tests/HaloAgent.Tests.csproj   # 全量，老用例零回归
```

- 新增 `Stage07HistoryTests` 6 用例全绿（见 02 §7）。
- 备份文件断言：截断后会话目录出现 `messages.replay-bak-*.jsonl`；分支后新会话目录出现 `messages.branch-src-*.json`。

## 2. 前端合闸

```powershell
cd src/Web.Client
npx vue-tsc -b
npx vite build
```

- `vue-tsc` 零 error；`vite build` 产物含 `MessageContextMenu` chunk（或内联，无 404）。
- Mock 下（浏览器直开 `vite dev`）走通 §3 全表。

## 3. 手工矩阵（12 格，真后端 + Mock 各跑一遍）

| # | 操作 | 步骤 | 预期 |
|---|------|------|------|
| H1 | 复制 user | 右键首条 user → 复制 → 粘贴到记事本 | 逐字一致 |
| H2 | 复制 assistant | 右键某轮 → 复制 → 粘贴 | markdown 源码一致，无思考/工具文本 |
| H3 | 复制空正文轮 | 构造纯工具轮 → 复制 | 吐司“本轮无正文可复制”，剪切板不变 |
| H4 | 重放中段 user | U1 A1 U2 A2，对 U1 重放 | 时间轴剩 [] 后复发 U1；落盘 U1' A1'，无双 U |
| H5 | 重放 assistant | 对 A2 重放 | 删 A2（含工具）及后续 → 重发 U2 → 新 A2' |
| H6 | 重放无前驱 | 脏数据孤立 assistant → 重放 | 吐司“找不到上一条用户消息”，不动盘 |
| H7 | 分支 user | 对 U2 分支 | 新卡置顶切卡，历史=[U1 A1]，草稿=U2 原文（可改后发送） |
| H8 | 分支 assistant | 对 A1（含工具）分支 | 新卡历史=[U1 A1 T*]，草稿空 |
| H9 | 生成中互斥 | 发送后立即右键 → 看菜单 | 重放/分支置灰，复制可用；点置灰无反应 + tooltip |
| H10 | 分支首条 user | 对首条 U 分支 | 新卡空历史 + 草稿有文 |
| H11 | 重放末条 | 对末条 U 重放 | 等价重生成一次 |
| H12 | 审计备份 | H4/H7 后看会话目录 | `*.replay-bak / *.branch-src` 存在且可打开 |

## 4. 数据一致性检查

1. 重放后 `meta.json: messageCount == messages.jsonl 行数`；`Cumulative*` 不减少（截断不清零）。
2. 分支后新 `meta: messageCount == prefix 行数`；`Cumulative*=0`；`Provider/Model` 与源一致。
3. 预算：重放复发完成一轮后顶栏水位与持久 footer 同源（沿用 need05 口径，不新增断言）。

## 5. 回归红线（任一命中即打回）

- `messages.jsonl` 出现半截行 / 轮次被劈开（assistant 与其 tool 分离到切面两侧）。
- 生成中可点重放/分支并真正写盘。
- `vue-tsc/vite/dotnet` 任一红。
- Mock 与真后端同操作不同结果（切面/草稿/禁用三处必须一致）。

## 6. 合闸签字

- [ ] 后端 6 单测绿 + 全量绿（贴 `dotnet test` 尾 5 行）。
- [ ] 前端 `vue-tsc + vite` 绿（贴尾 5 行）。
- [ ] H1–H12 双端（真机 + Mock）全过（贴关键截图：菜单 / 重放前后 / 分支新卡+草稿）。
- [ ] 备份文件抽查 1 组可打开。
