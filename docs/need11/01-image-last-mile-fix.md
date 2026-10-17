# need11-01 图片发送最后一公里修复（落盘→base64→双协议）

> 路径：`docs/need11/01-image-last-mile-fix.md`；单文档交付，只改后端+单测，不动前端/存储格式。
> 前置：`need09-03/05` 已落地；基线为当前 `main`（`MultimodalPlanner`+双 `Adapter`+`ViewImageTool`+`HostBridge` 粘贴图）。

## 1. 诊断（3 行结论）

1. **序列化形状正确**：Chat `type=image_url + image_url{url,detail}`（`ChatCompletionsAdapter.cs:130-137,150-159`）；Responses `type=input_image + image_url(一层字符串)`（`ResponsesApiAdapter.cs:87-90`），与标准一致。
2. **门控正确**：`MultimodalPlanner.PlanTurn` 非视觉抛 `NonVisionModelException`（`UnifiedLlmModels.cs:93`）+ `AgentOrchestrator.cs:267-297` 轮首预剥离/保最新，`SupportsVision=false` 默认（`ProviderModels.cs:87`）。
3. **生产断链**：`ImageResolver` 从未赋值（`AgentOrchestrator.cs:318-327` 无该字段），`ImageDataRegistry.Register` 全仓零调用，`ToDataUrl` 只用于前端缩略图。`PlanTurn:76` 经空注册表得 `null` → Adapter `Where(DataUrl非空)` 全过滤 → 静默纯文本发送。单测因手动注入 `_ => "data:..."`（`Stage09MultimodalAdapterTests.cs:90,114`）才全绿。

另带两个小缺口：Responses 丢 `detail`（Chat 有、Resp 无）；Responses `tool` 附图分支只发纯 `input_image` 无 `input_text`（`:137-143`），严格网关可能拒收。

## 2. 目标 / 非目标

* 做：① 补“读盘→dataURL”解析器并在 `RunAgentTurnAsync` 内注入；② Responses 补 `detail` + `tool` 附图带文本；③ 单测覆盖真盘/丢失/重启三态。
* 不做：改 `messages.jsonl` 格式（仍只存 `ChatImageRef`，永不存 base64）；改前端/缩略图/`view_image` 语义；引新依赖；改 `detail` 默认值（仍 `high`）；`file_id` 上传模式（留扩展位）。

## 3. 设计（最小增量，约 70 行）

核心：**废弃全局 `ImageDataRegistry` 写路径，改每轮按需读盘 + 本轮内存缓存**。理由：注册表是内存态，重启即空，而附件在盘上；按需读盘天然支持重启/多会话并发，且无全局锁。

```csharp
// 新增：SessionRepository.cs -> ImageAttachmentStore
public static string? ResolveDataUrl(string dataRoot, string wsId, string sessId, ChatImageRef img)
{
    try {
        if (img == null || string.IsNullOrWhiteSpace(img.ImageId)) return null;
        string? abs = FindImagePath(dataRoot, wsId, sessId, img.ImageId.Trim());
        if (abs == null || !File.Exists(abs)) return null;
        byte[] bytes = File.ReadAllBytes(abs);          // 归一化后文件一般 ≤2MB，≤16 张可接受
        if (bytes.Length == 0 || bytes.Length > MaxImageBytes) return null;
        string mime = MimeFromExtension(abs);
        if (!CheckMagic(bytes, mime)) return null;
        return ToDataUrl(bytes, mime);                   // "data:<mime>;base64,..."
    } catch { return null; }                             // 解析永不抛，缺图即丢图保文
}
```

```csharp
// 接线：AgentOrchestrator.cs RunAgentTurnAsync，requestMessages 就绪后、new UnifiedLlmRequest 前
var imgCache = new Dictionary<string, string?>(StringComparer.Ordinal);
Func<ChatImageRef, string?> imgResolver = img => {
    if (img == null || string.IsNullOrWhiteSpace(img.ImageId)) return null;
    string key = img.ImageId.Trim();
    if (imgCache.TryGetValue(key, out var hit)) return hit;
    string? url = ImageAttachmentStore.ResolveDataUrl(dataRoot, workspaceId, sessionId, img);
    imgCache[key] = url;                                 // null 也缓存：本轮不再重复读丢失文件
    return url;
};
// 构造 request 时加一行：
ImageResolver = imgResolver,
```

```csharp
// Responses 补 detail + tool 附图带文本（ResponsesApiAdapter.cs）
foreach (var r in kept) {
    writer.WriteStartObject();
    writer.WriteString("type", "input_image");
    writer.WriteString("image_url", r.DataUrl!);
    writer.WriteString("detail", VisionDefaults.NormalizeDetail(request.Model.VisionDetail)); // 新增
    writer.WriteEndObject();
}
 // tool 分支（现 :137-143 纯图）：首位补一个 input_text（严格网关要求 content[0]=input_text 且 trim 非空）
 // 复用 tool 原文（view_image 成功时恒为“已读取…”非空，与 Chat 侧 :125-126 对称）；空才回退常量，不用空格（空格会被 trim 判空，绕不过）
 string toolText = string.IsNullOrWhiteSpace(msg.Content) ? "图片如下：" : msg.Content;
 writer.WriteStartObject();
 writer.WriteString("type", "input_text");
 writer.WriteString("text", toolText);
 writer.WriteEndObject();
```

`ChatCompletionsAdapter` 不动（已正确）。`ImageDataRegistry` 保留（单测用），生产不再依赖；`llm-trace` 仍只记 `imgCount/imgTruncated`，不记 base64（`LlmGatewayClient.cs:286-295` 不动）。

## 4. 文件改动清单

| 文件 | 改动 | 行数 |
|---|---|---|
| `Infrastructure/Storage/SessionRepository.cs` | `ImageAttachmentStore` 新增 `ResolveDataUrl`（上） | +18 |
| `Services/AgentOrchestrator.cs` | 构建 `imgResolver` + `ImageResolver = imgResolver` | +12 |
| `LlmGateway/Adapters/ResponsesApiAdapter.cs` | `input_image` 后加 `detail`；`tool` 附图分支首位加 `input_text`（复用 tool 原文，空回退“图片如下：”） | +8 |
| `tests/.../Stage09MultimodalAdapterTests.cs` | 断言 Responses `detail`；新增真盘/丢失/超限 3 用例（下） | +60 |

`VisionDefaults.NormalizeDetail/ResolveMaxImages`、`MultimodalPlanner`、`ViewImageTool`、`HostBridge` 均不动。

## 5. 边角（实现时必守）
 
* 缺图/被删/魔数不对 → 返回 `null`，Adapter 已有 `Where(DataUrl非空)` 过滤，文字照发；`SessionRepository` 读时 `IsMissing` 标记不变，前端“已丢失”提示不变。
* 裸 base64 兼容：`PlanTurn:80-84` 已处理无 `data:` 前缀自动拼 `mime`，`ResolveDataUrl` 输出恒带前缀，直通即可。
* 内存：单轮上限 16 张×8MB，最坏 ~128MB 瞬时 JSON；`Utf8JsonWriter` 流式写 + `StringContent` 一次发送可承受，不做分片（与现 `SendRequestAsync` 一致）。
* 缓存作用域为单轮 `RunAgentTurnAsync` 局部变量，无跨会话污染，无锁；`FindImagePath` 跨日期目录扫描已存在（`:659-673`），直接复用。
 
## 6. 验收

* `dotnet build HaloAgent.sln` + `dotnet test --filter "Stage09"` 全绿；新增用例：
  1. 真盘：`SaveImage` 写临时盘 → 不注 `resolver` 改走 `ResolveDataUrl` → Chat payload 含 `image_url.url == data:...`；
  2. 丢失：删附件文件 → payload 无 `image_url/input_image` 且文本仍在；
  3. Responses：`VisionDetail=low` → `input_image.detail == "low"`；`tool` 附图分支 `content[0].type == "input_text"` 且非空（tool 文空时为“图片如下：”）。
* 手工：视觉模型发粘贴图 + `view_image` 图各 1 张，`llm-trace.log` 见 `imgCount=2` 且上游返回图片描述；切非视觉模型，同会话重发只见 `[图片已忽略]` 且 HTTP 200；删附件文件重发，文字成功、图静默丢弃。
* 回滚：仅 revert 本篇 3 个后端改动点，`messages.jsonl`/前端无格式变更，可直接回退。
