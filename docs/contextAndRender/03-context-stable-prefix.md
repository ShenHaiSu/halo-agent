# 高缓存命中（上）：稳定前缀的 System Transcript

源码：`packages/agent-runtime/src/system-transcript.ts`，
`system-transcript-journal.ts`，`transcript-compat.ts`，
`plugin-skills-prompt.ts`，`project-instructions-prompt.ts`，
`mode-prompts.ts`，`fixed-tool-declarations.ts`。

## 1. 设计目标：一句话

**让每轮 provider 请求的前 N 字节尽可能与上一轮完全相同**，命中 Anthropic prompt caching / OpenAI prompt cache。做法：固定排序 + 增量补丁 + 易变内容后置。

## 2. `desktopSectionNames`：写死的拼接顺序

`system-transcript.ts:38-42`：

```ts
["runtime", TOOL_ACTIVATION_SECTION, "skills",
  ...skillSections.sort(localeCompare),  // SKILL_SECTION_PREFIX*
  "context"]
```

`systemPromptContent`（L44-51）按此顺序 `join("\n\n")`。扩展 sections 永远排在桌面自有 sections 之后（注释 L35）。

为什么：

- `runtime`（ persona + 工具协作 + scratch 规则，`runtime.ts` 约 L2060-2108 组装）最稳定，放最前，缓存命中率最高。
- `skills` 按名排序，保证插件增删不改变其余 skills 的相对位置。
- `context`（预算水位、session 即时状态）最易变，放最后。即使它变了，前缀仍命中。

复刻时：**不要用 `Object.keys` 自然顺序**，必须显式排序表。新增 section 一律插到 `context` 之前。

## 3. `syncSystemSections`：增量补丁而非重写

`system-transcript.ts:53-65`：

```ts
for (name of desktopSectionNames({...current, ...desired})) {
  next = desired[name] ?? null;
  if (current[name] !== next) sections[name] = next; // 只记 diff
}
if (diff 非空) messages.push({ role:"system", content:"", sections: diff, timestamp: max+1 });
```

- pi-ai 的 `getCurrentSystemMessage` 会把 system 链 fold 成当前视图，diff 行只覆盖变了的 section。
- `timestamp` 取 `max+1` 且永不回拨（`nextSystemTimestamp` L18-21），否则会复活已失效的旧 usage。
- 无变化直接返回原数组（引用相等），上层可借此跳过重建请求。

`initialSystemTranscript`（L67-83）：老会话无 baseline 时在 continuation 边界声明当前 tools，不伪造历史指令。

`replaceSystemPrompt`（L85-89）：custom SYSTEM.md 只换 `runtime`，保留 `tool_activation`。

## 4. checkpoint 与恢复：只在 compaction 边界折叠

`systemTranscriptCheckpoint`（L110-119）：用 journal 的 durable 形状（含扩展文本块），并**剥掉 `context_budget` section**——水位是瞬时的，不能进摘要。

`rebuildSystemTranscript`（L91-107）：compaction/恢复后把之前 system 行按前后锚点插回原相对位置，system 行永远不跑到 user/assistant 中间打散前缀。

`removeTrailingAssistantMessages`（L122-130）：失败恢复丢掉尾部 assistant（跳过 system），pi-agent-core 拒绝 assistant 结尾的 transcript（`continue()` 要求 user 结尾）。

## 5. `transcriptCompat`：能力声明要网关验证

`transcript-compat.ts:30-39`：五个传输能力

```
supportsMidConvoSystemMessages / supportsMidConvoToolAdditions /
supportsMidConvoToolChanges / supportsAdditionalTools / supportsToolSearch
```

只有 `catalog.source=="pi"|"models.dev"` **且** `transcriptBinding{modelId,api,baseUrl} == 当前请求`（endpoint 归一化比对）才判 true。防止“用 A 模型的 middle-system 能力去调 B 网关”导致整轮 400。`buildProviderModel` 中它盖在最顶层（见 01 §3）。

## 6. 注入源的稳定性纪律

- `mode-prompts.ts / project-instructions-prompt.ts / project-memory-prompt.ts`：长静态文本一次生成后缓存，变量（workspace 路径、mode 名）放段尾。
- `plugin-skills-prompt.ts :: SKILL_SECTION_PREFIX`：每个 skill 独立 section，名排序，内容变化只触达自己一行 diff。
- `subagent-context.ts`：子代理上下文单独裁剪，不进主 transcript 前缀。
- `hosted-search-replay.ts :: restoreHostedSearchReplay`：持久化的 replay 数组在进上下文边界才 `normalizeHostedSearchContent`；坏块整条降级 `[]`（与旧记录一致），损坏记录抛 `LocalRequestError("context-validation")` 走本地错误通道，不污染 provider 请求。

## 7. 反模式（本项目明确拒绝的）

1. 把时间戳/水位/token 数插进 `runtime` 头部——前缀每轮都变，caching 全灭。
2. 用“是否可能开边界”做启发式跳过 tail parse（`markdown-blocks.ts` 头注释有同类论述）——第二套文法必与渲染文法分歧。
3. 全量重写 system（`content` 字符串替换）代替 sections diff——工具声明抖动会让网关以为 tools 变了。

## 复刻清单

1. 建 `desktopSectionNames` 白名单 + 排序，拼接只认它。
2. `sync` 只 push diff 行，timestamp 单调增，无 diff 返回原引用。
3. 易变（水位/时间/用量）全部塞进最后一个 section。
4. 能力 flag 必须绑定 `modelId+api+endpoint` 三元组验证。
5. checkpoint 剥瞬时 section；恢复保持 system 相对位置。
