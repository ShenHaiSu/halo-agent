using System.IO;
using System.Threading;
using System.Collections.Generic;
using Host.Desktop.Core.Models;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.LlmGateway.Adapters;

/// <summary>
/// 大模型底层通信协议适配器契约（Chat Completions 与 Responses API）
/// </summary>
public interface ILlmProtocolAdapter
{
    /// <summary>
    /// 所属协议类型
    /// </summary>
    LlmProtocolType Protocol { get; }

    /// <summary>
    /// 计算目标请求端点完整 URL
    /// </summary>
    string ResolveEndpointUrl(string baseUrl);

    /// <summary>
    /// 将 UnifiedLlmRequest 序列化为对应协议的标准 JSON 请求载荷
    /// </summary>
    string SerializeRequest(UnifiedLlmRequest request, bool includeUsageInStream = true);

    /// <summary>
    /// 将底层 HTTP 响应数据流解析为统一的 UnifiedStreamEvent 流
    /// </summary>
    IAsyncEnumerable<UnifiedStreamEvent> ParseStreamAsync(Stream responseStream, CancellationToken ct = default);

    /// <summary>
    /// 诊断并格式化非 200 HTTP 错误响应
    /// </summary>
    string FormatErrorResponse(int statusCode, string responseBody);
}
