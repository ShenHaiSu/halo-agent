# need09-05 双协议多模态底层 + view_image 自主看图工具 — 前端可施工细化版

> 前置：`01/02` **已冻结**，`03/04` 已细化。本篇后端 payload 冻结不变，前端渲染部分细化到组件。
> 现状证据：`UnifiedChatMessage` 纯文本；`MessageTimeline.vue` user 气泡为 `whitespace-pre-wrap` 纯文本、assistant 走 `renderMarkdown(v-html)` + 思考工具舱；`ToolCallCard.vue` 按 `card{callId/functionName/argsJson/status/resultText}` 渲染；`turnGroup.ts` 归并轮次；`exportSessionMarkdown` 拼纯文本。

## 1. 消息模型与 Bridge（前后端同构，精确到 TS）

```ts
export interface ChatImageRef {
  imageId: string; fileName: string; mime: string; relPath: string;
  width: number; height: number; sizeBytes: number; sha16: string;
}
export interface UnifiedChatMessage {
  // …存量不动…
  images?: ChatImageRef[] | null;  // 缺字段/null/空 = 纯文本
}
// 新增 Bridge（只传缩略图，不传原图，防 CefSharp 字符串超限）：
getImageThumbnail(workspaceId: string, sessionId: string, imageId: string)
  => Promise<BridgeResponse<string>>  // data:image/png;base64,...（长边≤256px）
```

- `turnGroup.ts`：`images` 参与轮次归并键（同 assistant + tool 对逻辑不变；图片不拆轮，只跟随所属消息）。
- `exportSessionMarkdown`：图片导出 `![fileName](relPath)`（不嵌 base64）；丢失图片导出 `[图片已丢失：fileName]`。

## 2. 时间轴渲染（`MessageTimeline.vue` 改动，精确到位置）

### 2.1 user 气泡（现有 `item.kind==='user'` div 内追加图片行）

```html
<div class="...whitespace-pre-wrap...">
  {{ msg.content }}
  <div v-if="msg.images?.length" class="mt-1.5 flex flex-wrap gap-1.5" data-testid="msg-images">
    <n-image v-for="img in msg.images" :key="img.imageId" :src="thumbOf(img)" :alt="img.fileName"
             width="96" object-fit="cover" class="rounded border"
             :data-testid="`msg-image-${img.imageId}`" />
  </div>
  <div v-if="hasLostImage(msg)" class="text-[11px] text-amber-500">部分图片已丢失（文件被删除）</div>
</div>
```

- `thumbOf`：优先用 `thumbnailCache(imageId)`（Map；miss 则调 `getImageThumbnail` 异步回填，失败置 `lost`）。
- 右键菜单：`openMenu` 的 `text` 仍取纯文本（图片不进复制；复制图片走点击放大后右键存图，浏览器原生行为）。
- tool 消息图片（`view_image` 结果）：在轮次舱 `ToolCallCard` 内同样式缩略图行（见 §3），时间轴持久 tool 灰块同样复用该行（`toCard(t)` 扩展 `images` 透传）。

### 2.2 assistant 流式（`hasActiveTurn` 容器）

- 本期 assistant 不产生图片流；流式容器不加图片行（预留 `streamingImages` 空数组，不渲染，避免误接）。
- `TurnCompleted → finishTurn` 后持久消息含图，时间轴按 §2.1 渲染（快照清空逻辑不动）。

### 2.3 样式与性能

- 缩略图统一 96px（`w-24`），点击 `n-image` 预览原缩略图（非原图；原图查看走 `openPathInExplorer` 按钮，`ToolCallCard` 内 `view_image` 成功卡加“打开原图”链接）。
- 同一 `imageId` 跨消息复用同一缩略图缓存（sha16 去重天然对应，见后端 05 §1.2）。
- `renderMarkdown` 不解析图片占位符（占位符后端已剥离，前端只认 `images` 数组，不做文本 `[image:]` 正则替换——避免与用户原文冲突）。

## 3. `ToolCallCard.vue`（`view_image` 卡精确到文案）

| 状态 | 显示 |
|---|---|
| running | `正在读取图片：<path>`（argsJson 的 path 截断 60 字） |
| done | 绿色 `已读取 <fileName> (WxH, KB)` + 缩略图 96px + `打开原图` + 结果文本折叠（存量 `resultText`） |
| error | 红色 + 自愈提示原文（路径非法/类型不支持/超限三类文案与后端一致，禁止前端改写） |

- `view_image` 的 `resultText` 截断规则与存量一致（`truncateText 6000`，base64 永不进卡）。
- `data-testid`：`tool-card-view_image / tool-image-<imageId> / tool-open-image-<callId>`。

## 4. 后端 payload（冻结回顾，施工对照）

- chat 有图 user：`content:[{type:text},{type:image_url:{url:data:...,detail}}]`；无图保持 `content:string`（零抖动）。
- resp 有图 user：`content:[{type:input_text},{type:input_image:{image_url:data:...}}]`。
- tool 图：`function_call_output.output` 仍文本摘要，图片字节由网关按 `Images` 引用组装，不进 tool 文本。
- 估算 `+1000/image`，裁剪先丢图保文（`[image omitted]` 占位）。

## 5. 施工清单（本篇内）

1. TS `ChatImageRef` + `images` + `getImageThumbnail` + Mock 占位缩略图。
2. `MessageTimeline` user 图片行 + tool 图片行（经 `toCard` 透传）+ 丢失占位。
3. `ToolCallCard` view_image 三态 + 打开原图。
4. `turnGroup/export` 图片兼容。
5. 联调 H4/H5/H6（见 08）。

## 6. 边角表（前端必测）

| # | 边角 | 预期 |
|---|---|---|
| E1 | 图片文件被删 | 缩略图位显示“已丢失”，文字保留，可重放 |
| E2 | 老消息无 images | 纯文本渲染，与改前逐像素一致 |
| E3 | 非视觉模型历史含图 | 照常渲染缩略图（渲染与能力位无关） |
| E4 | 16 张超限会话 | 时间轴全显（裁剪只影响发送，不影响存储渲染） |
| E5 | `getImageThumbnail` 失败 | 显示文件名 chip，不崩时间轴 |
## 7. 后端附录（冻结，施工对照，前端可跳过）
> 本节为后端施工保留的冻结语义，与前端 §1–§6 无冲突。
### 7.1 C# 模型（`WorkspaceModels.cs`，缺字段即纯文本）
```csharp
public sealed class ChatImageRef {
  public string ImageId { get; set; } = "";   // img_<sha16[..16]>
  public string FileName { get; set; } = "";
  public string Mime { get; set; } = "";      // image/jpeg|image/png|image/webp
  public string RelPath { get; set; } = "";   // 会话内相对路径 attachments/<date>/<imageId>_<safe>
  public int Width { get; set; } public int Height { get; set; }
  public long SizeBytes { get; set; } public string Sha16 { get; set; } = "";
}
// UnifiedChatMessage 新增 List<ChatImageRef>? Images（null/空=纯文本；STJ 缺字段容忍，序列化 WhenWritingNull）
```
### 7.2 持久化
- `messages.jsonl` 只存引用不存 base64；字节文件 `<DataRoot>/sessions/<ws>/<sess>/attachments/<yyyyMMdd>/<imageId>_<safeName>`；粘贴图与 view_image 图按 sha16 去重。
### 7.3 view_image（`Tools/BuiltIn/ViewImageTool.cs`，第 7 号工具）
- Schema：`{path必填（相对拼 WorkspaceRoot 经 PathSafetyValidator；绝对允许工作区外但拒绝系统目录/_index.json）, detail?: auto|low|high}`。
- 流程：空路径 Fail → 相对/绝对判定 → 扩展名+魔数头（FFD8/PNG89/RIFF-WEBP）→ ≤8MB → 归一化（sha16去重→System.Drawing长边≤2048→png/jpg q85副本）→ `ToolResult.Success(摘要文本, Images=[ref])`。
- `ToolResult` 新增 `List<ChatImageRef>? Images`；`ToolOutputGuard` 只截文本不截图；`ToolExecutionCompleted.resultText` 只含摘要（base64 永不进事件/日志）；`llm-trace` 记 imgCount/sha16/尺寸。
