# need09 施工方案总览（Skill / Memory 双域系统 + 多模态输入 + 图片工具 + 问询交互）

> 输出路径：`docs/need09/`；方案先行，代码按篇施工。
> 状态：`01 双域存储` 与 `02 装配线契约` **已冻结（2026-10-16 用户确认）**；`03/04/05/06` 已按可施工粒度细化到组件/事件/testid，可直接施工；`07/08` 语义不变。
> 基线：`need08` 落地时刻的代码（`AgentOrchestrator(MaxIterations=25/effectiveMax 快照 + ToolContext 超时快照)`、`SystemPromptBuilder.BuildCore(identity/runtime/cmdPython/toolGuide/ephemeral/custom)`、`TokenBudgetService(92%高水位/70%低水位 + 原子轮次裁剪)`、`PrefixCachePredictor(5块拼接对比)`、`ChatCompletionsAdapter/ResponsesApiAdapter(纯文本 content)`、`ToolRegistry(6标准工具)`、`PromptComposer(n-input textarea 纯文本)`、`ModelConfig{Id/DisplayName/Protocol/MaxContext/MaxOutput/Temperature/Enabled}`、`StoragePathManager(DataRoot/config/workspaces/sessions/logs)`）。
> 目标读者：后端 + 前端 + 联调施工同学；按本文顺序施工即合闸。
> 冗余目标：本次所有新增必须为未来（音频/视频/技能市场/记忆检索/RAG/按会话覆盖/云同步）留出扩展位，且不破坏 `messages.jsonl` 行格式与现有 6 工具语义。

## 1. 一句话目标

引入 **Global（全局，随 Agent 程序数据根走）与 Project（随工作区 `.haloagent/` 走）双域 Skill / Memory 系统**，以**极稳定、低 token、高缓存命中**的方式拼入上下文；模型增加**多模态（看图）能力位**；主界面输入框彻底重做为**粘贴感知 + 富文本图片 Tag**；`chat/resp` 双协议底层兼容**多模态消息**；新增 **3 类默认工具**（`view_image` 自主看图、`ask_user` 阻塞式问询、`skill/memory` CRUD），形成“存得下、拼得稳、看得见、问得到、管得住”的完整闭环。

## 2. 九项需求与文档映射（必读顺序）

| 需求 | 文档 | 回答的问题 | 主要触点 |
|---|---|---|---|
| R1 双域 Skill/Memory 存储 | `01-skill-memory-storage.md` | Global/Project 各存哪、目录结构、命名、索引、并发、迁移、配额 | `StoragePathManager`、`SkillMemoryService`、`.haloagent/` |
| R2 稳定低 token 拼接 + 高缓存命中 | `02-stable-prompt-cache.md` | 拼什么顺序、分级装载、stable/ephemeral 切分、排序、哈希、裁剪、预测 | `SystemPromptBuilder`、`TokenBudgetService`、`PrefixCachePredictor`、`LlmGatewayClient` |
| R3 模型多模态能力位 | `03-model-vision-capability.md` | `supportsVision` 存哪、校验、降级、UI、网关门控 | `ProviderModels`、`ProviderConfigService`、`ProviderManagerView` |
| R4/R5 输入框粘贴 + 富文本 Tag | `04-composer-paste-richtext.md` | 粘贴如何分辨图文、附件模型、芯片渲染、取消、发送载荷 | `PromptComposer`、`agentStore`、`hostBridge` |
| R6/R7 双协议多模态 + view_image | `05-multimodal-protocol-and-view-image.md` | 消息模型如何扩展、双协议序列化、持久化、预算、`view_image` 语义 | `WorkspaceModels`、双 `Adapter`、`SessionRepository`、`ViewImageTool` |
| R8 ask_user 阻塞问询 | `06-ask-user-interaction.md` | YAML/JSON 格式、遮罩交互、等待/取消/超时、回填格式 | `AskUserTool`、`AgentOrchestrator`、`AskUserModal`、Bridge |
| R9 skill/memory 工具 CRUD | `07-skill-memory-tools.md` | 几个工具、action 枚举、权限、校验、热生效 | `SkillTools`、`MemoryTools`、`ToolRegistry` |
| 联调/测试/发布 | `08-integration-test-rollout.md` | 怎么证明做对了、合闸命令、回滚 | `tests/HaloAgent.Tests`、`vue-tsc/vite/dotnet`、手工矩阵 |

依赖关系：`01 → 02 → 03 → 04 → 05 → 06 → 07 → 08`。`01/02` 未冻结不得开工 `04/05/06/07`（顺序即前缀稳定性契约）。

## 3. 范围一句话

- **做**：双域目录 + 索引 + 配额；稳定前缀装配线（L0 常驻索引 + L1 按需加载）；模型 `supportsVision`；输入框重做（粘贴/拖拽/附件芯片/取消）；消息多模态扩展 + 双协议图片序列化 + `view_image`；`ask_user` 阻塞交互；`skill/memory` 管理工具；预算/压缩/日志/单测同步改造。
- **不做**：音频/视频多模态；记忆向量检索/RAG；技能市场/云同步/版本时间线；按工作区/会话/模型分套 Skill 覆盖（仅留 `scope` 扩展位）；服务端下发；本轮热切换 Skill（下一轮生效）；改动网络超时与模型预算语义。

## 4. 全局施工原则（八篇通用，冲突以各篇“精确到行”为准）

1. **零破坏只增量**：不动 `messages.jsonl` 已有纯文本行的反序列化（缺字段即纯文本）；不动 6 标准工具名/语义；`ModelConfig` 新增字段必须有默认值（缺省即兼容）；`AgentOrchestrator` 构造签名保留兼容。
2. **稳定前缀是最高铁律**：任何稳定区（system.stable + tools + skill 索引）禁止按时间/热度重排；一律按 `name` 升序；开关变化只允许整块出现/消失，不允许块内抖动；`custom` 默认放 `tail` 且为非稳定（沿用 need06 语义）。
3. **下一轮生效**：Skill/Memory/模型能力/提示词装配的任何变更，均在下一轮 `RunAgentTurnAsync` 起生效；流式中/工具执行中不重载快照。
4. **失败回默认不断会话**：Skill/Memory 文件缺失/损坏/超限一律跳过 + `Warn` 日志 + 前端横幅（若可见），会话仍可正常发起；`Save` 一律 `Copy→.bak→原子写`。
5. **图片永不进日志与前缀哈希裸奔**：`llm-trace` 只记 `imageCount/hash/尺寸`，不记 base64；`prefixHash` 对图片用 `sha16(归一化引用)` 而非裸字节，避免微小重编码导致缓存雪崩。
6. **Win7/CefSharp109 红线**：禁止升级 `.NET6/CefSharp109/Vite chrome109`；禁止引入原生 `rg/skia/onnx` 依赖；图片编解码只用 `System.Drawing.Common(Windows)` 或纯字节头解析 + 前端 `canvas` 缩放；YAML 解析手写最小子集（JSON 为主，YAML 为糖）。
7. **可测性**：所有新增 Bridge 方法配 `data-testid`；后端 `dotnet build + dotnet test`，前端 `vue-tsc -b && vite build`；`08` 全表跑通方可合闸。

## 5. 强制施工顺序

```text
Step A 冻结 01（双域路径 + 目录结构 + 命名 + 索引格式 + .haloagent 规范全员评审）
Step B 冻结 02（装配线顺序 + L0/L1 切分 + stable/ephemeral 切线 + 排序 + 哈希 + 裁剪规则）
  └─ 后端先行：SkillMemoryService + SystemPromptBuilder 新块 + TokenBudget/PrefixCache 扩展（单测全绿）
Step C 03（ModelConfig.supportsVision + 校验 + 网关门控 + ProviderManagerView UI）
  └─ 跑：dotnet build + dotnet test（老用例不变）+ vue-tsc -b && vite build
Step D 04（PromptComposer 重做：粘贴/拖拽/芯片/发送载荷 + Mock 双端一致）
Step E 05（UnifiedChatMessage 多模态 + 双 Adapter 图片序列化 + 持久化 + view_image）
Step F 06（ask_user：Schema → 阻塞等待 → 遮罩 UI → 回填；与 04 遮罩互斥）
Step G 07（skill/memory 管理工具：action 枚举 + 权限 + 校验 + 热生效）
Step H 联调 08（缓存命中 + 多模态 + 问询 + CRUD × 真后端 × Mock 一致 × 迁移不丢）
```

禁止 `01/02` 未评审就写 `04/05/06/07`；禁止单测未绿写前端；禁止跳过 `08` 合闸。

## 6. 验收总表（详情跳转各篇）

- [ ] 01：Global/Project 路径冻结；`skill/<name>/readme.md` + `memory/<name>/readme.md` 结构冻结；非法名/遍历/超限矩阵全过；迁移后不丢。
- [ ] 02：稳定区字节稳定（同输入两次 `prefixHash` 一致）；L0/L1 切分 token 达标；超限裁剪先丢图后裁文且不断 tool 对；预测 `divergenceBlock` 可解释。
- [ ] 03：`supportsVision=false` 时图片被拦截并自愈提示；`true` 时双协议均可发出图片；老 `providers.json` 缺字段可读。
- [ ] 04：粘贴图片/文本/混合三态正确；芯片显示文件名 + 缩略图 + 叉号取消；发送载荷与显示一致；大图受限且有提示。
- [ ] 05：`chat/resp` 均可发图；`messages.jsonl` 存引用不存 base64；重载渲染一致；`view_image` 相对/绝对路径 + jpg/png/webp 全过。
- [ ] 06：YAML/JSON 双格式解析一致；遮罩期间输入框不可用；逐题回答 + 自定义输入 + 跳过语义正确；取消/超时不死锁。
- [ ] 07：Skill/Memory 增删改查 + 启用禁用全过；越权（写 Global/Project 错域、遍历）被拒；下一轮生效。
- [ ] 08：`dotnet + vue-tsc + vite` 全绿；手工矩阵全过；存储迁移后 Global Skill/Memory 不丢，Project 随工作区走。
