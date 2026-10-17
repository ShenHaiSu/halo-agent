# need09-01 双域 Skill / Memory 存储系统（Global vs Project）

> 前置：`00-README.md`。本篇冻结后才允许开工 02/04/05/06/07。
> 现状证据：当前仅有全局 `config/prompt.json`（`PromptConfigService`）、`config/agent.json`（`AgentConfigService`）、`config/providers.json`（`ProviderConfigService`）三份全局配置，无 Skill/Memory 概念；存储根由 `StoragePathManager` 两级引导（`bootstrap.json → C:\HaloAgentData → LocalAppData 降级`，子目录 `config/workspaces/sessions/logs/cef-cache`）；工作区仅有 `RootPath` 文件夹绑定，无工作区级配置目录。

## 1. 概念定义（冻结）

| 概念 | 定义 | 类比 | 体量目标 |
|---|---|---|---|
| **Skill** | 可复用的“能力包”：一段稳定的提示词片段（HowTo）+ 可选的资产文件（脚本/模板/示例）+ 元数据（名称/版本/开关/优先级）。模型通过“读索引 → 按需读正文 → 调资产”渐进使用。 | Claude Skills / Cursor Rules | 单个 Skill 正文 ≤ 4000 字符；索引行 ≤ 120 字符 |
| **Memory** | 持久化的“知识/偏好/事实”：用户或模型长期沉淀的记录（项目简介、技术栈、禁忌、常用命令）。分为常驻（pinned，拼入前缀）与归档（按需读）。 | Memory / AGENTS.md | 单条 Memory ≤ 4000 字符；常驻总 ≤ 8000 字符 |

Skill 偏“怎么做”，Memory 偏“是什么/记住什么”。两者共享同一套双域存储、索引、版本、开关机制，但**提示词块 ID 不同**（`skillIndex/skillBody` vs `memoryPinned`），便于 02 分别做稳定/裁剪策略。

## 2. 双域路径（冻结）

### 2.1 Global 域（随 Agent 程序数据根走）

```
<DataRoot>/
  skills/
    <skill-name>/
      readme.md            # 必需：Skill 正文（首段为一句话简介 + 可选 YAML frontmatter）
      assets/              # 可选：任意文件（.py/.ts/.md/.json/.txt），模型可通过 view/read 工具读取
      skill.json           # 可选：机器元数据（若缺失则从 readme frontmatter + 默认值推导）
    _index.json            # 自动生成的全局索引（服务维护，禁止手改；损坏可重建）
  memory/
    <memory-name>/
      readme.md            # 必需：记忆正文
      assets/              # 可选
      memory.json          # 可选
    _index.json
```

- `<DataRoot>` 即 `StoragePathManager.CurrentDataRoot`（默认 `C:\HaloAgentData`，可迁移）。Global Skill/Memory **随数据根迁移走**（纳入 `MigratableSubDirectories` 新增 `skills/memory` 两项，见 §7）。
- “Agent 程序默认存储位置”在本项目中即数据根，不要另起 `%ProgramFiles%` 或程序目录（Win7 权限 + 迁移一致性原因）。

### 2.2 Project 域（随工作区根目录走）

```
<WorkspaceRoot>/
  .haloagent/
    skills/
      <skill-name>/readme.md (+ assets/ + skill.json 可选)
      _index.json
    memory/
      <memory-name>/readme.md (+ assets/ + memory.json 可选)
      _index.json
    config.json            # 预留：工作区级开关（本期只存 version + skill/memory 总开关，余字段保留）
```

- `.haloagent/` 为工作区根下的隐藏配置目录（git 建议忽略，见 §8）。删除工作区时不删源代码，只删绑定关系；`.haloagent/` 随项目文件夹走（换电脑拷项目即带走记忆，符合直觉）。
- 工作区合法性校验（`WorkspaceSessionService`）必须容忍 `.haloagent/` 不存在（即“零 Skill/Memory 的普通项目”），不存在即视为空域，不报错。

### 2.3 为什么不是 `config/skills`？

`config/` 下已有 `prompt.json/agent.json/providers.json/app-settings.json` 全是单文件 JSON。若把 Skill 正文塞进 `config/` 会导致：单文件膨胀、并发写冲突、`.bak` 语义混乱、迁移时无法按 Skill 粒度审计。独立顶层 `skills//memory/` 目录与现有 `workspaces//sessions//logs/` 平级，最符合 `StoragePathManager.ManagedSubDirectories` 扩展习惯。

## 3. 文件结构规范（冻结，01/07/02 共用）

### 3.1 目录名（skill-name / memory-name）

- 正则：`^[a-z0-9][a-z0-9-_]{1,47}$`（小写字母数字 + `-_`，2–48 字符，首字符字母数字）。
- 禁止：`..`、`/ \`、`.` 开头、`_index`、`_tmp`、`con/prn/aux/nul/com1~9/lpt1~9`（Windows 保留名）。
- 大小写不敏感唯一（`MySkill` 与 `myskill` 视为同一名，后写覆盖前者归一化为小写）。

### 3.2 readme.md（正文，必需）

```markdown
---
version: 1
enabled: true
priority: 100
pin: auto        # skill: auto(仅索引常驻,正文按需) | full(正文也常驻,慎用)
                 # memory: auto | pinned(常驻) | archived(仅按需)
scope_hint: "适用场景一句话（同时作为索引行后半段）"
updated_at: "2026-10-16T00:00:00Z"   # 可空，服务自动维护
---

# <标题>

正文（Markdown，≤ MaxChars，见 §6 配额）。
首段第一句必须是一句话简介（用于索引行兜底）。
```

- 解析规则：若存在 `---` frontmatter 则解析上述 5 键（未知键保留但忽略，大小写不敏感）；无 frontmatter 则全文即正文 + 默认元数据。
- `skill.json / memory.json`（可选）与 frontmatter 并存时，**`*.json` 优先**（机器写盘用），`readme.md` frontmatter 为人类手改入口；读取时合并（json 缺键则取 frontmatter，仍缺则取默认值）。

### 3.3 assets/（可选）

- 任意文件，但受 §6 配额（单文件 ≤ 512KB，总 ≤ 5MB/域/类）。二进制（图片）允许存放，模型通过 `view_image`/`read_file` 按相对路径读取。
- 路径引用规范：正文/工具中引用资产一律用 `assets/<file>` 相对 Skill 根的 POSIX 风格相对路径；服务层再拼接为绝对路径 + 沙箱校验（禁止 `../` 逃逸）。

### 3.4 _index.json（自动生成，禁止手改）

```json
{
  "version": 1,
  "updatedAtUtc": "2026-10-16T00:00:00Z",
  "skills": [
    { "name": "git-helper", "scope": "global", "enabled": true, "pin": "auto",
      "priority": 100, "version": 1, "title": "Git 助手",
      "summary": "常用 git 流程与冲突处理", "chars": 1234, "estTokens": 420,
      "hash": "9f86d081884c7d65", "hasAssets": true }
  ]
}
```

- `hash = SHA16(规范化正文)`（规范化 = `\r\n→\n + TrimEnd + frontmatter剥离`），用于 02 前缀哈希与变更检测。
- 启动/读写后由 `SkillMemoryService.RebuildIndex` 原子重写；读取时若损坏则重建 + `Warn` 日志，不中断会话。

## 4. 后端服务设计（精确到类）

### 4.1 新增 `Services/SkillMemoryService.cs`（唯一出入口）

```csharp
public enum SkillMemoryScope { Global, Project }
public enum SkillMemoryKind { Skill, Memory }
public sealed record SkillMemoryEntry {
  string Name, Scope, Title, Summary, BodyMarkdown, Pin;
  bool Enabled; int Priority, Version, EstTokens; string Hash; bool HasAssets;
  string DirPath; DateTime UpdatedAtUtc;
};
public sealed class SkillMemoryService {
  SkillMemoryService(StoragePathManager storage);
  // 读
  IReadOnlyList<SkillMemoryEntry> List(SkillMemoryScope scope, SkillMemoryKind kind, string? workspaceRoot);
  SkillMemoryEntry? Get(...name...);
  string ResolveAssetPath(...);           // 含 ../ 逃逸校验
  (global, project) GetMergedForPrompt(string? workspaceRoot); // 02 消费：去重+排序后的快照
  // 写（07 工具与未来 UI 共用）
  SkillMemoryEntry CreateOrUpdate(...);   // 原子写 + 索引重建 + 配额校验
  void Delete(...);                        // 删除目录 + 索引重建（保留 .bak 一份，见 §5）
  void SetEnabled(..., bool enabled);
  void EnsureInitialized();                // 建目录 + 空索引
  void RebuildIndex(...);
}
```

- 并发：内部 `object _syncLock` 串行化写（对标 `AgentConfigService`）；读无锁（读文件 + 内存快照，写后失效）。
- 缓存：`GetMergedForPrompt` 结果按 `(scope, workspaceRoot, indexMtime)` 做 5 秒内存快照，避免每轮 IO；`RunAgentTurn` 每轮只调一次（见 02）。
- 错误：任何单 Skill 损坏（缺 readme/超限/非法名）→ 跳过该条 + `HaloLogger.App.Warn`，不污染整域。

### 4.2 `StoragePathManager` 扩展（最小增量）

- `ManagedSubDirectories` 新增 `"skills"`, `"memory"`；`MigratableSubDirectories` 同步新增（Global 随迁移）。
- 新增只读属性：`GlobalSkillsDirectory => Path.Combine(CurrentDataRoot,"skills")`，`GlobalMemoryDirectory` 同理。
- `EnsureStandardSubdirectories` 自动建新目录；老数据根升级时 `EnsureInitialized` 补建，不删旧文件。
- Project 域路径**不经过** `StoragePathManager`（工作区根由 `WorkspaceSessionService` 管理），由 `SkillMemoryService.ResolveProjectRoot(workspaceRoot)` 拼接 `".haloagent"`，并做 `Path.GetFullPath + 工作区根前缀校验`。

### 4.3 `WorkspaceSessionService` 触点

- `AddWorkspace` 后自动 `SkillMemoryService.EnsureProjectInitialized(workspaceRoot)`（建空 `.haloagent/skills|memory`，失败只 Warn）。
- `RemoveWorkspace` **不删** `.haloagent/`（数据归项目，不归 Agent）；仅解绑。
- 工作区改名/移动时 `.haloagent/` 随文件夹走，无需额外逻辑。

## 5. 写盘原子性与备份（对标 agent.json）

- 单 Skill/Memory 写盘顺序：`Validate → 写 tmp(uuid) → Flush → Move 覆盖 readme.md/(json) → 重建 _index.json（同样原子）`。
- 删除：先 `Copy 目录 → <name>.deleted-<ts>.bak（同级）` 再删原目录，保留 1 份（防模型误删）；前端/日志提示 bak 路径。
- 并发保存：`lock(_syncLock)` 后写 wins；`updated_at` 服务端强制 `UtcNow`（忽略客户端传入，避免时钟倒退导致缓存误判）。

## 6. 配额表（冻结，后端强制，前端提示）

| 项 | 上限 | 超限行为 |
|---|---|---|
| 单 `readme.md` 字符 | 8000（约 ~2000 tokens） | 拒绝保存 `SKILL_VALIDATION`，指明截断到哪 |
| 单资产文件 | 512KB | 拒绝写入 |
| 单域单类总字符（Global-skills 等 4 格） | 60000 字符 | 新建/更新时拒绝，提示先删或归档 |
| 单次提示词装入（L0 常驻，见 02） | skills 索引 ≤ 1500 tokens；memory pinned ≤ 2000 tokens | 超限按 `priority↓ + name↑` 截断并记 `truncated:true` |
| 名称/数量 | 单域单类 ≤ 200 条 | 拒绝新建 |

## 7. 迁移与兼容

- `ChangeDataRootAsync(migrateExistingData=true)` 必须连带拷贝 `skills//memory/`（复用 `CollectMigratableFiles`，只需把新目录加入白名单）；`migrate=false` 则新盘补空目录，老盘保留（用户可手动拷）。
- 老版本无 `skills//memory/` 目录 → `EnsureInitialized` 自动补空，`Load` 返回空列表（非失败）。
- `_index.json` 版本字段：`version != 1` → 重建（向前兼容，禁止抛异常中断启动）。

## 8. .haloagent 规范与 git 协作

- 建议 `.gitignore` 追加（文档提示，不强制改用户仓库）：
  ```
  .haloagent/tmp/
  .haloagent/**/*.bak
  ```
  `skills//memory//readme.md + assets/` 默认应提交（项目记忆随代码走）；`_index.json/config.json` 可提交可忽略（可重建）。
- 模板：新建工作区时可选种 1 个示例 `memory/project-brief/readme.md`（项目简介模板，默认 `archived`，不占常驻 token），由前端“初始化项目记忆”按钮触发（未来 UI，本期仅留 Bridge 扩展位）。

## 9. 明确不做（防蔓延）

1. 不做向量检索/RAG/语义搜索（本期仅清单 + 全量索引常驻 + 按需全文，见 02 L0/L1）。
2. 不做按会话/模型分套覆盖；`scope` 字段写死 `global|project`，预留 `session/model` 枚举值但后端拒绝写入。
3. 不做导入导出 zip/云同步/版本历史 diff（仅 `.bak` 一份覆盖式备份）。
4. 不做 Skill 定时任务/后台 hook（`hooks/` 目录名预留但禁止执行）。

## 10. 边角表（02/07/08 必须覆盖）

| # | 边角 | 预期 |
|---|---|---|
| E1 | 非法名（`../x`、`CON`、超长） | 拒绝 `SKILL_VALIDATION`，不写盘 |
| E2 | `readme.md` 缺失但目录存在 | 该条跳过 + Warn，下轮重建索引时消失 |
| E3 | `_index.json` 损坏/空 | 重建 + Warn，会话不报错 |
| E4 | 工作区根被删/不可读 | Project 域视为空，不阻塞 Global |
| E5 | Global 与 Project 同名 Skill | 合并时 Project 优先（覆盖 Global），`mergedFrom=project-overrides-global` 打标，02 详述 |
| E6 | 存储迁移中途取消 | bootstrap 未更新，旧盘仍生效；新盘半拷贝文件下次迁移覆盖（幂等） |
| E7 | assets 引用 `../../secret.txt` | `ResolveAssetPath` 拒绝 `PATH_TRAVERSAL` |
