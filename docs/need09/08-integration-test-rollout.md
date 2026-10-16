# need09-08 联调测试与发布计划（合闸门禁）

> 前置：`00–07` 全部冻结。本篇是合闸唯一依据。
> 门禁命令（与 need08 同口径）：
> ```bash
> dotnet build HaloAgent.sln -c Debug
> dotnet test HaloAgent.sln -c Debug
> pnpm --dir src/Web.Client exec vue-tsc -b
> pnpm --dir src/Web.Client build
> ```

## 1. 后端单测（`tests/HaloAgent.Tests` 新增 Stage09*，老用例零改动）

| 用例组 | 覆盖 | 断言要点 |
|---|---|---|
| `Stage09SkillMemoryStorageTests` | 命名/路径/配额/索引/迁移白名单 | 非法名拒绝；`_index.json` 损坏重建；`ChangeDataRoot` 携带 `skills/memory` |
| `Stage09StablePromptTests` | 装配线字节稳定 | 同快照两次 `BuildCore` 逐字节一致；空域时无 `## 5./## 6.` 标题；排序与截断确定性 |
| `Stage09VisionModelTests` | 能力位兼容 | 老 `providers.json` 缺字段回退 `false/4`；`MaxImages>16` 拒绝 |
| `Stage09MultimodalAdapterTests` | 双协议序列化 | 无图 payload 与 need08 基线逐字节一致；有图 chat 含 `image_url`、resp 含 `input_image`；`supportsVision=false` 抛 `NonVisionModelException` |
| `Stage09ViewImageTests` | 路径/类型/魔数/越权 | `../` 拒绝；`gif` 拒绝；系统目录拒绝；`sha16` 去重同一文件 |
| `Stage09AskUserTests` | 解析/校验/超时/并发 | YAML 糖与 JSON 等价；5 题拒绝；双 pending 第二个 Fail；超时不挂起（用 1s 超时测） |
| `Stage09SkillToolTests` | CRUD/默认域/配额 | 缺 scope 默认写 project；`update` 不存在拒绝；超 8000 拒绝 |

## 2. 前端门禁与 Mock 一致性

- `vue-tsc -b && vite build` 全绿；`chrome109` 无高版本语法（contenteditable 代码避免 `Array.at(-1)` 等，CefSharp 109 不支持需 polyfill 或改写）。
- `hostBridge.ts` Mock 必须与真后端同语义：`startAgentTurn` 第 6 参附件回放、`resolveAskUser` 假答案、`truncate/branch` 存量语义不动、`getImageThumbnail` 返回 1px 占位图。
- `data-testid` 清单：`composer-rich-input/att-remove-*/composer-send/model-supports-vision-switch/ask-user-modal/ask-user-submit/skill-*`（手工矩阵逐项点检）。

## 3. 手工矩阵（真后端 × Win10/Win11 各跑一次，Win7 跑冒烟子集）

| # | 场景 | 步骤 | 预期 |
|---|---|---|---|
| H1 | 双域 Skill | 建 Global skill + Project 同名 skill，发一轮 | 提示词索引两行，Project 覆盖生效（02 E5） |
| H2 | 缓存命中 | 同一会话连发两轮纯文本追问，查 `llm-trace` | 第二轮 `divergenceBlock=latest`，`cached` 上升 |
| H3 | 粘贴图片 | 微信截图 Ctrl+V 到输入框 | 芯片显示文件名 + 缩略图 + ×；× 可取消 |
| H4 | 发送图片 | H3 后 Enter（视觉模型） | 时间轴显示缩略图；`messages.jsonl` 有 `images` 无 base64 |
| H5 | 非视觉模型发图 | 切 `supportsVision=false` 模型重发 H4 | 黄字警告 + 自动纯文本降级 + 通知 |
| H6 | view_image | 工作区放 `a.png`，让模型 `view_image(path=a.png)` | Tool 卡显示成功 + 下轮模型能描述图片 |
| H7 | ask_user | 让模型问 2 选题 | 遮罩 + modal 逐题 + 提交后 ReAct 继续 |
| H8 | skill CRUD | 让模型 `skill_tool(create→get→update→delete)` | 逐项确认 + 下轮生效提示；文件落盘正确 |
| H9 | 迁移 | 改存储路径（迁移数据） | `skills/memory` 跟随，新盘生效，旧盘保留 |
| H10 | 损坏回退 | 手动损坏某 `readme.md` 为空 + `_index.json` 乱写 | 会话可发，Warn 日志，索引重建 |

## 4. 性能与费用验收

- 无 Skill/Memory 纯文本会话：整包 payload 与 need08 基线逐字节一致（`prefixHash` 不变，费用零增加）。
- 20 Skill + 10 Memory 会话：常驻增量 ≤ 3500 tokens（L0 配额内）；日常追问缓存命中率（`cached/input`）不低于无 Skill 时的 90%。
- 单张 8MB 粘贴图：从粘贴到芯片出现 ≤ 500ms（仅读头 + objectUrl，不压缩）；发送到落盘归一化 ≤ 3s（Win7 机械盘放宽到 5s）。

## 5. 发布与回滚

- 发布：`build-dist.cmd`（win-x64 全量 + win-x86 冒烟）→ Win7 SP1 启动 → 新建 Workspace/Session → H1–H8 跑通 → 改存储路径 → 重启数据不丢。
- 回滚：任何一篇验收失败，整批 need09 代码回滚到 need08 基线（`messages.jsonl` 新 `images` 字段老版本忽略，前向安全）；`.haloagent/` 目录保留（用户数据不删）；`providers.json` 新字段老版本忽略（后向安全）。
- 日志排查入口：`logs/llm-trace-*.log（REQ prefixHash/divergence/imgCount）` + `logs/app-*.log（[Skill]/[Agent]/[Image] Warn）` + `startup-trace.txt`。
