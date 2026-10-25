using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Host.Desktop.Core.Models;

/// <summary>
/// S6 §4.1/§4.2：图片位置契约的唯一事实源（纯函数，无 IO、无静态可变状态，可并行单测）。
///
/// 背景：前端草稿有位置（segments 里 image 段），但过 Bridge 时占位符被改写成
/// `[图片：fileName]` 纯文本 —— 位置信息与 imageId 同时丢失，adapter 只能把图追加到文末。
/// 本类把机器可解析的**有序引用**形态固定下来：
///   - 新形态（S6 起）：`[image:img_&lt;sha16前8&gt;]`（imageId 前 15 字符；人类可读性略降，转录渲染换回 fileName）
///   - 旧形态（S6 前老历史）：`[图片：fileName]`
///   - 兼容形态：`[image:att_xxx]` / `[image:1]`（前端原占位）→ `OrderImagesByPlaceholder` 对能解析出 imageId 的项排序，
///     其余（att_/纯数字）无图可对，视为“无锚点”，退化为数组序。
///
/// 设计原则（与 S5 §4.3 的“退化原则”一致）：删了锚点就掉文末/数组序，绝不因为占位符缺失而错位或崩。
/// </summary>
public static class ImagePlaceholder
{
    /// <summary>S1 起的“图片被丢弃”占位（与 TokenBudgetService.ImageOmittedPlaceholder 同值，避免跨层依赖）。</summary>
    public const string OmittedPlaceholder = "[image omitted]";

    /// <summary>占位符里 imageId 的最大截断长度（`img_` + sha16 前 8 位 = 12，留一点余量给老格式）。</summary>
    private const int ShortIdMaxLength = 15;

    // 机器可解析形态：`[image:<id>]`；id 仅允许字母数字、下划线与短横（与前端 composerModel 同字符集）。
    // 沿用仓库既有写法（Compiled 静态实例，与 TokenBudgetService.ImageRefRegex 同风格），不用 source generator。
    private static readonly Regex MachinePlaceholderRegexInstance =
        new(@"\[image:([A-Za-z0-9_-]+)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Regex MachinePlaceholderRegex() => MachinePlaceholderRegexInstance;

    /// <summary>
    /// 短 id：`img_&lt;sha16前8&gt;` 形态（已经是短形态则原样返回）。
    /// 输入为空/非法返回空串，调用方据此跳过重写（永不因占位符失败中断落盘）。
    /// </summary>
    public static string ShortId(string? imageId)
    {
        string id = (imageId ?? string.Empty).Trim();
        if (id.Length == 0)
            return string.Empty;
        if (id.Length > ShortIdMaxLength)
            id = id.Substring(0, ShortIdMaxLength);
        return id;
    }

    /// <summary>渲染新形态占位符：`[image:img_xxxxxxxx]`。</summary>
    public static string Marker(string? imageId)
    {
        string id = ShortId(imageId);
        return id.Length == 0 ? string.Empty : "[image:" + id + "]";
    }

    /// <summary>
    /// 把 user 文本里的机器占位符批量重写为 `<paramref name="renderer"/>` 形态（默认短 id 形态，幂等）。
    /// 老形态 `[图片：...]` 不动（已经是展示形态）。用于 Bridge 落盘前的最终形态化。
    /// </summary>
    public static string NormalizeMarker(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;
        return MachinePlaceholderRegex()
            .Replace(
                text,
                m =>
                {
                    string marker = Marker(m.Groups[1].Value);
                    return marker.Length == 0 ? m.Value : marker;
                }
            );
    }

    /// <summary>
    /// 契约核心（S6 §4.1-2 / §5 验收 1~2）：按占位符在文本中的出现顺序对图片排序。
    ///
    /// 规则：
    /// 1. 文本里命中 `[image:&lt;id&gt;]` 的图，按**首次出现位置**排序（重复占位符只取首处）；
    /// 2. 文本里没有任何已知锚点的图，保持原数组相对顺序，沉底（兼容老历史/纯 tool 图）；
    /// 3. 无占位符 / images 为空 / text 为空 ⇒ 原数组顺序（零抖动，与现状字节一致）。
    /// </summary>
    public static List<ChatImageRef> OrderImagesByPlaceholder(
        string? text,
        IReadOnlyList<ChatImageRef>? images
    )
    {
        var result = new List<ChatImageRef>();
        if (images == null || images.Count == 0)
            return result;
        if (images.Count == 1 || string.IsNullOrEmpty(text))
        {
            result.AddRange(images);
            return result;
        }

        // 占位符出现顺序 → imageId 序号（首现为准）
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in MachinePlaceholderRegex().Matches(text))
        {
            string id = m.Groups[1].Value;
            if (id.Length == 0 || order.ContainsKey(id))
                continue;
            order[id] = order.Count;
        }
        if (order.Count == 0)
        {
            result.AddRange(images);
            return result;
        }

        // (排序键, 原始下标, 引用)：命中锚点按锚点序号，未命中按 int.MaxValue 沉底但保留相对序
        var keyed = new List<(int Anchor, int Index, ChatImageRef Ref)>(images.Count);
        for (int i = 0; i < images.Count; i++)
        {
            var img = images[i];
            int anchor = int.MaxValue;
            string id = (img?.ImageId ?? string.Empty).Trim();
            if (id.Length > 0 && order.TryGetValue(id, out int pos))
                anchor = pos;
            keyed.Add((anchor, i, img!));
        }
        keyed.Sort(
            (a, b) =>
            {
                int c = a.Anchor.CompareTo(b.Anchor);
                return c != 0 ? c : a.Index.CompareTo(b.Index);
            }
        );
        foreach (var item in keyed)
            result.Add(item.Ref);
        return result;
    }

    /// <summary>
    /// S6 §4.2-2：把图片按占位符顺序切成与发报一致的块序列。
    ///
    /// 输入：user 文本（已 NormalizeMarker）、按锚点排好序的图片（可被 S5 预算摘除后子集）、
    /// 以及每张图的解析结果 dataUrl（null/空白 = 该图在预算/读盘阶段被丢弃）。
    ///
    /// 输出：交错的块列表（Text / Image），可直接映射为 chat 的 `content:[{type:text},{type:image_url}]`
    /// 或 Responses 的 `input_text` / `input_image`。
    ///
    /// 退化（与现状字节一致）：文本里没有任何**未发送图**可断开的锚点 ⇒ 单个 Text 块 + 尾部追加图块
    /// （等价于 S6 之前的“文后追加”），保证老历史/纯 tool 图零抖动。
    ///
    /// 被丢弃（dataUrl 为空）的图：其占位符在 Text 块中渲染为 `[image omitted]`（复用 S1 常量，语义统一），
    /// 图块不产出 —— 模型因此既不幻觉看到图，也不会漏掉“这里本来有图”。
    /// </summary>
    public static List<ImageContentBlock> BuildContentBlocks(
        string? text,
        IReadOnlyList<ChatImageRef>? images,
        Func<ChatImageRef, string?>? resolveDataUrl
    )
    {
        string src = text ?? string.Empty;
        var blocks = new List<ImageContentBlock>();

        int emitted = 0; // 已产出图块数（决定是否发生真实交错）
        var anchors = new Dictionary<string, ChatImageRef>(StringComparer.Ordinal);
        if (images != null)
        {
            foreach (var img in images)
            {
                string id = (img?.ImageId ?? string.Empty).Trim();
                if (id.Length == 0 || anchors.ContainsKey(id))
                    continue;
                anchors[id] = img!;
            }
        }
        if (anchors.Count > 0)
        {
            // imageId → 本次是否可发（dataUrl 非空）。同一 imageId 只发首张，重复引用不重复发图。
            var sendable = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var kv in anchors)
                sendable[kv.Key] = !string.IsNullOrWhiteSpace(
                    resolveDataUrl != null ? resolveDataUrl(kv.Value) : null
                );
            int cursor = 0;
            var pendingText = new StringBuilder();
            var emittedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in MachinePlaceholderRegex().Matches(src))
            {
                string id = m.Groups[1].Value;
                bool known = sendable.TryGetValue(id, out bool canSend);
                // 先攒占位符之前的文本；「能否产图块」必须在 FlushText 之前判定，
                // 否则被摘除/去重的占位符会随之丢失（[image omitted] 落不到块里）。
                pendingText.Append(src, cursor, m.Index - cursor);
                cursor = m.Index + m.Length;
                if (known && canSend && emittedIds.Add(id))
                {
                    FlushText(blocks, pendingText);
                    blocks.Add(
                        new ImageContentBlock(ImageContentBlockKind.Image, null, anchors[id])
                    );
                    emitted++;
                }
                else
                {
                    // ① 同 id 第二处引用（去重，不发重复图块）② 预算/读盘摘除（known && !canSend）
                    // ③ 本消息身上已无任何可发图，但结构上仍是「带图消息」（unknown 且锚点非空）
                    // ⇒ 一律就地渲染 [image omitted]，既不错位也不凭空多出 text 块。
                    pendingText.Append(OmittedPlaceholder);
                }
            }
            if (cursor < src.Length)
                pendingText.Append(src, cursor, src.Length - cursor);
            FlushText(blocks, pendingText);
        }

        // 一张图块都没产出（老历史无锚点 / 纯 tool 图 / 全部被预算摘除）：
        // 需要区分两种情况，绝不能无脑 reset 回 src ——
        //   ① 上面**没有**扫描过占位符（anchors 为空）⇒ 文本原样可用，退化为「原文单块 + 文后追加」；
        //   ② 上面**扫描过**占位符（anchors 非空）⇒ 文本里的占位符已被改写成 [image omitted]，
        //      必须保留已产出的 Text 块（否则占位符会原样泄漏给模型）。
        if (emitted == 0)
        {
            if (anchors.Count == 0)
            {
                blocks.Clear();
                if (src.Length > 0)
                    blocks.Add(new ImageContentBlock(ImageContentBlockKind.Text, src, null));
            }
            if (images != null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var img in images)
                {
                    string id = (img?.ImageId ?? string.Empty).Trim();
                    if (id.Length == 0 || !seen.Add(id))
                        continue;
                    string? url = resolveDataUrl != null ? resolveDataUrl(img!) : null;
                    if (string.IsNullOrWhiteSpace(url))
                        continue;
                    blocks.Add(new ImageContentBlock(ImageContentBlockKind.Image, null, img!));
                }
            }
        }
        else if (
            blocks.Count > 0
            && blocks[^1].Kind == ImageContentBlockKind.Text
            && string.IsNullOrEmpty(blocks[^1].Text)
        )
        {
            blocks.RemoveAt(blocks.Count - 1); // 尾块空文本无意义（避免多发一个空 text 段）
        }
        return blocks;
    }

    private static void FlushText(List<ImageContentBlock> blocks, StringBuilder pending)
    {
        if (pending.Length == 0)
            return;
        blocks.Add(new ImageContentBlock(ImageContentBlockKind.Text, pending.ToString(), null));
        pending.Clear();
    }
}

public enum ImageContentBlockKind
{
    Text = 0,
    Image = 1,
}

/// <summary>S6：发包/渲染两用的内容块（Text 块带文本，Image 块带图片引用）。</summary>
public sealed record ImageContentBlock(
    ImageContentBlockKind Kind,
    string? Text,
    ChatImageRef? Image
);
