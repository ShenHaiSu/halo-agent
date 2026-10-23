# 08 破坏性切换 + 数据迁移 + 全量验收（P3）

## 1. 前置检查（P3 开工门槛）

- [ ] 01–07 验收全绿，无残留旧语义（全局搜索：`SendRequestAsync(/DefaultSafetyBuffer/KeepDefaultGroups/renderMarkdown(/SEG_FLUSH_MS/MD_CACHE_MAX` 只剩 08 迁移提示）。
- [ ] `dotnet build HaloAgent.sln` + `pnpm -C src/Web.Client typecheck lint test build` 全绿。
- [ ] 备份：全量 `dataRoot/messages.jsonl + meta` 快照。

## 2. 破坏性切换清单（一次性，不做开关）

| 旧语义 | 新语义 | 用户可见 |
|---|---|---|
| 固定 1024 buffer + 组数裁剪 | window 公式 + token 切点 | 水位一次性跳变 |
| 固定 1s/2s/4s 重试 3 次 | 分池 10 次 + Retry-After + 看门狗 | 弱网 turn 变长但可恢复 |
| 整源 markdown + hljs 同步 | 块级 memo + tail-only + shiki/mermaid | 首屏高亮闪一次 |
| 全量 timeline 重算 | history/tail + rAF 合批 | 切会话无白闪 |
| 自定义 Header 禁键可写 | 禁键清洗丢弃 | 违规头被丢并 Warn |

## 3. 数据迁移（一次性脚本/代码）

- 会话文件：`messages.jsonl` 信封不变，无需重写；首次打开旧会话时按 03 重建 sections（前缀变化一次），05 切点按新逻辑（不主动重压缩旧会话）。
- 配置：扫描 `CustomHeaders` 命中禁键的条目，启动时 Warn + UI 红点提示用户改到鉴权位，不自动改值。
- 缓存：删旧 `PrefixCachePredictor` 纯日志口径缓存与旧整源 markdown 缓存（启动时清内存即可，无盘文件则跳过）。

## 4. 全量验收矩阵

- 网络：弱网模拟（429 带 Retry-After/5xx/超时/代理断）→ 可恢复、无双计费、无 terminal 误判。
- 上下文：同会话连发 3 轮 stableHash 不变；加压到 90% 线触发内联压缩；压缩后续聊工具链不断。
- 渲染：200 轮会话流式 + 上翻 + 切会话 + 搜索定位，全程无掉帧、无抢滚、无白闪；46KB fence 与超限源降级正确。
- 账单：usage 有值时以真值为准，校准不改账单；取消/错误同样落袋但不重算。
- 安全：禁键头不可发送；mermaid/KaTeX 失败隔离；外链 noopener；本地图越界回 chip。

## 5. 出口标准

- [ ] 01–07 所有复选框全绿；本节矩阵全绿。
- [ ] 无 `// TODO(兼容旧语义)` 残留；回滚方案仅保留“整包回滚到重构前 tag”，不保留逐阶段回滚。
- [ ] 更新 `README` 重构说明 + `docs/pitfalls/` 新增一条重构记录。
