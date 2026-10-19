# 04 · 联调测试计划（先单测后联调，四处同源 + 前缀稳定 + /compact 端到端）

> 前置：01→02→03 后端 `dotnet build + dotnet test` 全绿，前端 `vue-tsc -b && vite build` 全绿。本篇是唯一的联调入口，禁止跳项。

## 1. 合闸命令（按序执行，一条红即停）

```bash
# 后端（workspace 根）
dotnet build HaloAgent.sln -c Release
dotnet test tests/HaloAgent.Tests/HaloAgent.Tests.csproj --filter "Stage05" -v n
dotnet test tests/HaloAgent.Tests/HaloAgent.Tests.csproj -v minimal   # 全量回归，AC03 等旧用例必须绿

# 前端（src/Web.Client）
pnpm exec vue-tsc -b
pnpm run build
# 全局残留检查（01 篇防回退）
grep -rn "telemetry.inputTokens +" src/Web.Client/src || echo "OK: no accumulate"
grep -rn "+= .*InputTokens" src/Host.Desktop/Services || echo "check manually (only Cumulative+= allowed)"
```

## 2. 联调脚本 A：三轮对账（目标 1+2）

1. 新会话连发 3 轮普通问答（无工具），每轮记录：`TurnCompleted.inputTokens/outputTokens/cachedTokens/billedPrompt`、`WorkbenchTopBar Used/%`、`MessageTimeline` 本轮与持久 footer、`<dataRoot>/sessions/{ws}/{sess}/meta.json` 的 `totalInputTokens/cumulativePromptTokens`、`llm-trace-*.log` 的 `[REQ]prefixHash` 与 `[RES]in/cached/hitRate`。
2. 期望：
   * `inputTokens` 缓增趋稳（如 48K→49K→50K），绝不出现 48K→96K→144K；
   * `billedPrompt` 单调 Σ（≈3 轮 prompt 之和），`Used` 与末轮 `IN` 差 <1%；
   * `cache≤IN`，`hitRate` 在未裁剪轮持平或上升；
   * 未裁剪轮 `prefixHash` 三轮完全相同。

## 3. 联调脚本 B：未知态与 ≈估算（目标 1）

1. 构造无快照会话：手工建 `messages.jsonl` 仅 user/tool 行（或用旧备份），`getContextBudget` → 顶栏显示 `?` 灰色，悬停卡 `占用未知`，不显示百分比数字。
2. 空会话：`used==0` 显示 `0/128K(0%)` 蓝色。
3. 流式中：`Used` 后缀 `≈+est`（如 `48.2K≈+0.3K`），`Usage` 到账后 `≈` 消失且数值不回 0。

## 4. 联调脚本 C：高水位块淘汰（目标 2）

1. 用小 `MaxContextTokens=8192` 模型发长历史至 92% 以上，观察一次裁到 ~70%：`notice` 出现、`tool` 组无孤儿（发一条带 `tool_calls` 的轮次验证无 `400`）、下下轮 `prefixHash` 稳定。
2. 跨天验证：`system` 的 `## 4. Session Ephemeral` 日期变化不影响首屏 `stable`（以 `llm-trace` 整包哈希变化但 `hitRate` 不归零为准，不做硬断言）。

## 5. 联调脚本 D：/compact 端到端（目标 3）

1. 15+ 条消息会话发 `/compact`：时间轴出现 `✔ 已压缩：N条收敛为摘要`、摘要块含四段中文要点、尾部 6 组完整、`messages.compact-bak-*.jsonl` 存在且行数=压缩前、`meta.json Cumulative*` 不变、顶栏变 `?` 灰色。
2. 下轮再发一条消息：占用恢复精确百分比，`Σ` 继续累加（压缩前后 `Σ` 差 = 压缩调用本身的 `prompt+completion`，在 `llm-trace` 可查）。
3. 短会话（≤8 条）`/compact` 提示“还短无需压缩”且无 `bak`；断网时提示失败原因且 `jsonl` 字节不变（`fc /b` 对比）。
4. 压缩中点发送被拒、中断无效，结束后自动恢复（录屏或文字记录即可）。

## 6. 回退预案

* 任一脚本红：先回退到 `need04-05` 基线（`git stash docs/need05` 改动外的代码改动），`AvailableInput` 与 `Sum` 两行联动回退，勿只回一半（见 04-05 §6）。
* `/compact` 数据事故：用同目录最新 `messages.compact-bak-*.jsonl` 覆盖 `messages.jsonl`，重启 `loadSessionMessages` 即恢复；`meta.json` 的 `MessageCount` 以重载 `jsonl` 行数为准手工校对。

## 7. 验收签字表（全勾方可合闸）

- [ ] §1 四条命令全绿，无 `+= InputTokens` 快照累加残留（除 `Cumulative+=` 两行）。
- [ ] 脚本 A 四处 `IN` 同源（顶栏/两处 footer/meta）+ `prefixHash` 稳定 + `hitRate` 不掉。
- [ ] 脚本 B 未知画 `?`、空会话 `0%`、`≈` 过渡正常。
- [ ] 脚本 C 块淘汰一次到位、无孤儿 tool、无逐轮抖。
- [ ] 脚本 D 压缩/短会话/失败/并发四态正确、`bak` 与 `Σ` 正确。
- [ ] `ExportSessionMarkdown` 头部口径与 01 篇一致（占用/累计分开，无 `In X/Out Y` 旧二元）。
