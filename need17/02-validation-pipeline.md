# need17-02 · 中央校验管线：错误在执行前被拦下，并指明改法

> 前置：01（Schema 结构已定）。对应 pi `toolDocs/03 §3.3`（三道清洗）+ `02 §2.5`（准备四步）。
> 目标：任何畸形参数都止步于 `Dispatch` 之前，失败文案含 **code + 字段路径 + 期望 + 实际收到的 JSON 摘要 + 下一步**；正确参数被归一化后才进 handler。

---

## 2.1 总装位置：`DispatchAsync` 包一层，不动 handler 签名

现状 `Tools/ToolRegistry.cs:140-229`：查 handler → 直接 `ExecuteAsync`。各 handler 内部分散解析（错类型静默忽略是最大坑）。

方案：新增 `Tools/Validation/` 三文件，`DispatchAsync` 插入一步（伪码）：

```
查 handler（找不到→UNKNOWN_TOOL；找到但不在激活集→TOOL_NOT_ENABLED）
→ ToolCallValidator.Validate(toolName, rawArgsJson)
  → 失败：FailCode(code, message, payloadJson) 直接返回，不进 ExecuteAsync，不计时
  → 成功：normalizedArgsJson 交 handler 执行；warnings 拼接到工具结果 Error/Output 尾部（仅提示，不判失败）
→ 老异常兜底 / ToolOutputGuard 保持不动
```

* `IToolHandler.ExecuteAsync` 签名**不变**（15 个文件零改动）；handler 内部的业务校验（OLD_STRING_NOT_FOUND、NEED_CONFIRM_OVERWRITE 等 need15 码）保持为第二道防线。
* `ToolResult` 新增可选字段 `ValidatedWarnings: List<string>?`（`WhenWritingNull` 忽略，老消费方无感），`AgentOrchestrator` 回填时把 warnings 以 `Warning: ...` 行拼到 tool 消息尾（06 同步消费）。

### 验收

* `Validator_Blocks_Before_Execute`：spy handler 计数器证明校验失败时 `ExecuteAsync` 零调用。
* 老回归：`Stage05ToolExecutionTests`、`Stage15*` 全绿（行为不变）。

## 2.2 `ToolCallValidator` 四步（冻结顺序）

新建 `Tools/Validation/ToolCallValidator.cs`（纯函数 + 注册表驱动，无 IO）：

1. **Parse**：空串→`{}`；`JsonDocument.Parse` 失败→`INVALID_JSON_ARGUMENTS`（message 含解析错误前 200 字符 + 原 args 前 500 字符）。
2. **Shim**（`ToolParameterShims.cs`，按工具名分表，见 2.3）：别名搬运、形状修复、类型宽容转换。shim 产出 `normalized JsonObject + warnings`。
3. **Required**：缺失必填→`MISSING_REQUIRED_PARAM`（message：`缺少必需参数 'limit'（工具 edit_lines 需要 file_path,offset,limit,new_text）。收到的参数：{...摘要}。`）。
4. **Type/Enum/约束**：逐字段按 01 生成的 Schema 校验（string/integer/boolean/enum/minLength/maxLength/minimum/maximum）→ `TYPE_MISMATCH` / `ENUM_VIOLATION` / `CONSTRAINT_VIOLATION`。message 模板冻结：
   `TYPE_MISMATCH：参数 'offset' 应为 integer（1-based 行号），实际收到 string "10"。请传数字 10。收到的参数：{...}。`

* 未知多传字段：`additionalProperties:false` 生效时→`UNKNOWN_ARGUMENT`（指明删掉哪个字段；这是 00 §3 承诺的唯一 breaking，需单测锁定文案）。
* 数字宽容：JSON 字符串 `"10"`→整数 10（integer 字段）、`"true"`→bool、double 10.0→10（仅恰为整数时）。宽容成功记 warning，不拒绝。
* 截断摘要：收到的 JSON 摘要超 500 字符截断 + `...`，避免错误消息本身爆上下文。

### 验收（`Stage17ValidationTests`，≥12 用例）

* 字符串数字/布尔被宽容接受；`offset:10.5` 被拒；`mode:"LINE"` 被拒并列出枚举；`old_string` 短于 `minLength` 被拒；未知字段被拒并点名；`{path:...}` 搬运成功且 warning；单对象 `edits` 形状（如有）被修复；空 args、坏 JSON、未知工具各一例。

## 2.3 Shim 表（各工具一行，无分支膨胀）

| 工具 | shim 动作 |
|---|---|
| 全工具 | `path`(string)→`file_path` 搬运（若后者缺失），warning `ALIASED_PARAM` |
| `exec_command` | `timeout`/`timeoutMs`(number\|string)→`timeout_ms`；`"30s"`→30000（支持 `s` 后缀）；`cmd`→`command` |
| `read_file`/`list_dir`/`grep_search` | 数字字符串→数字（offset/limit/max_depth）；`limit:0/负数` 留给业务层（不提前拒，语义各工具自定） |
| `edit_file` | `oldString/newString`（小驼峰）→蛇形；`old_string` 为 JSON 字符串且能解析→保持原样交业务层（不猜） |
| `write_file` | `content` 非 string（如数字）→拒（不做 `ToString` 猜，避免整文件写坏）；`allowOverwrite` 小驼峰→蛇形 |
| `edit`（兼容） | 缺 `mode`→默认 `text` + warning（老调用不断）；`mode` 非法→`ENUM_VIOLATION`（02 统一拒，不再进分发器） |

原则冻结：**shim 只做无歧义修复**（别名、大小写、数字字符串）；有歧义的一律拒绝并指明改法，绝不猜（如 `content` 类型错、JSON 字符串多义）。

## 2.4 错误码附录（冻结，后续文档只追加）

| Code | 抛出点 | 含义 |
|---|---|---|
| `UNKNOWN_TOOL` | Dispatch | 工具名不存在（含可用列表，存量语义码化） |
| `TOOL_NOT_ENABLED` | Dispatch | 工具存在但不在本轮激活集（含 preset/点名指引） |
| `INVALID_JSON_ARGUMENTS` | Validator-Parse | args 不是合法 JSON |
| `MISSING_REQUIRED_PARAM` | Validator-Required | 缺必填（含缺失字段名+该工具必填全表） |
| `TYPE_MISMATCH` | Validator-Type | 字段类型错（含 path+期望+实际） |
| `ENUM_VIOLATION` | Validator-Type | 枚举值非法（含可选值全表） |
| `CONSTRAINT_VIOLATION` | Validator-Type | minLength/maxLength/minimum/maximum 违反 |
| `UNKNOWN_ARGUMENT` | Validator-Type | 多传未知字段（additionalProperties） |
| `PYTHON_NOT_AVAILABLE` | exec_command | 本机无 Python（07 专项，码定于此，文案定于 07） |

* 全部走 `ToolResult.FailCode`（`Error` 首 token 即码，复用 need15 约定）；payloadJson 放 `{tool, missing:[...]}` 等机器可读结构。
* `HintForTool` 为每个新码加一句话（06 落子，02 先占位）。

### 验收

* `ErrorCodes_Frozen`：上表 9 个码各至少 1 个单测断言 `Error.StartsWith("CODE")`；`ErrorCode` 字段非空。
