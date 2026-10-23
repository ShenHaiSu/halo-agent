# 03 稳定前缀 System Transcript（P1-1）

第二参考：`contextAndRender/03-context-stable-prefix.md`。本项目收益最大的一步。

## 1. 前置检查

- [ ] 通读 `SystemPromptBuilder.cs + SystemPromptBuilder.Index.cs`：标出哪些块是稳定的（工具表、skill 索引、纪律）与易变的（日期、workspace 路径、配额水位、mode 名）。
- [ ] 通读 `PrefixCachePredictor.cs::SplitSystem/SplitStableSystem`：确认现有 stable 切分标记（`SkillIndexTitle/MemoryPinnedTitle`），记录与字节稳定性的差距。
- [ ] 确认 `TurnPlanner.BuildRequestAsync` 当前是单字符串 `SystemInstructions + history`，无 sections 概念。
- [ ] 确认无 `pi-agent-core` transcript fold：本项目 folding 自建，不抄 `getCurrentSystemMessage`。

## 2. 阶段目标

- System 按写死顺序拼接：`runtime → tool_activation → skills(名排序) → context(易变尾部)`。
- 同一会话连续两轮：前 N 字节完全相同（易变只在尾部）。
- 增量补丁 + journal + checkpoint 剥瞬时 + 恢复保序。

## 3. 实现办法

### 3.1 新建 `SystemTranscript.cs`（Services）

- `SectionOrder = ["runtime", ToolActivationSection, "skills", ...SkillNames.Sorted, "context"]`。禁止 `Object.keys`/字典序自然拼接；新增 section 一律插到 `context` 之前。
- `BuildSystemText(sections)` = 按序 `Join("\n\n")`。扩展 sections 永排桌面自有 sections 之后。
- `SyncSystemSections(current, desired)`：只 diff 有变 section；有 diff 则 append `{role:system, sections:diff, timestamp:max+1}`；无 diff 返回原引用（上层借此跳过重建）。
- `timestamp` 单调增永不回拨；`ReplaceSystemPrompt` 只换 `runtime`，保留 `tool_activation`。
- 注入纪律：长静态文本一次生成后缓存；变量（路径、mode 名、日期、水位）一律放段尾；每个 skill 独立 section。

### 3.2 journal 与 checkpoint

- 新建 `SystemTranscriptJournal.cs`：`persist()` 在 dispatch 前持久化 provider-neutral 声明，行结构 `{id, role:system, version:1, messageJson, before/after}`；`orderRows()` 按 before/after 重排 + 去重；失败抛本地错误阻止发送（不污染网关请求）。
- `SystemTranscriptCheckpoint()`：剥掉 `context`（水位瞬时）再进摘要输入；`RebuildSystemTranscript()` 把 system 行按前后锚点插回原相对位置，永不跑到 user/assistant 中间打散前缀。
- `RemoveTrailingAssistantMessages`：失败恢复丢尾部 assistant（跳过 system）。

### 3.3 能力门 `TranscriptCompat.cs`

- 简化版（只有 Chat/Resp）：`supportsMidConvoSystem / supportsToolChanges` 必须绑定 `modelId + protocol + endpoint(归一化)` 三元组；换模型/换端点即判 false，防止“A 模型能力调 B 网关”整轮 400。

### 3.4 接入 `TurnPlanner`

- `BuildRequestAsync` 改为：`SystemTranscript.Sync → SystemInstructions(稳定前缀) + budgeted history`；`PrefixCachePredictor` 从纯日志转正为稳定 hash 输入（`stableHash/fullHash` 双记沿用 `PromptLastSentStore`）。

## 4. 验收标准

- [ ] 单测：调换 section 插入顺序不影响输出顺序；skill 增删只动自己一行 diff。
- [ ] 单测：无变化时返回原引用；timestamp 永不回拨；checkpoint 不含水位文本。
- [ ] 集成：连续两轮 `stableHash` 一致，`fullHash` 仅当尾部变化；`llm-trace` 可见前缀命中率上升。
- [ ] 反模式：`runtime` 头部无时间戳/token 数；无第二套启发式文法；无全量字符串替换 system。
- [ ] 旧 `BuildCore` 单字符串语义删除（破坏性），`BuildPreview` 同步新顺序。

## 5. 风险

- 旧会话恢复时首轮前缀必然变化（一次性），按 08 提示用户，不兼容。
