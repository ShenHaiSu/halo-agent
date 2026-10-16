using System.Collections.Concurrent;
using Host.Desktop.Core.Models;

namespace Host.Desktop.Services;

 /// <summary>need06-03 §2.3：上轮实际发送快照（内存，最近1条/会话，不落盘）。</summary>
 public sealed class LastSentPromptSnapshot
 {
     public string ReqId { get; set; } = string.Empty;
     public string SessionId { get; set; } = string.Empty;
     public string WorkspaceId { get; set; } = string.Empty;
     /// <summary>兼容保留：恒等于 FullHash（老 Bridge 按整包 system 文本对比，行为不变）。</summary>
     public string PrefixHash { get; set; } = string.Empty;
     /// <summary>need09-02：stable 区哈希（SHA16(stable)，Skill/Memory 变更即变；命中归因用）。</summary>
     public string StableHash { get; set; } = string.Empty;
     /// <summary>need09-02：整包 system 文本哈希（SHA16(full system)，恒等于 PrefixHash）。</summary>
     public string FullHash { get; set; } = string.Empty;
     public int CharLength { get; set; }
     public string SentAtUtc { get; set; } = string.Empty;
 }

 /// <summary>need09-02：stable/full 双哈希与当前预览的对比结果（“稳定区是否变化”一目了然）。</summary>
 public sealed record PromptHashComparison(bool HasLastSent, bool StableEqual, bool FullEqual);

 public static class PromptLastSentStore
 {
     private static readonly ConcurrentDictionary<string, LastSentPromptSnapshot> Store = new(StringComparer.Ordinal);

     public static void Record(string sessionId, LastSentPromptSnapshot snapshot)
     {
         if (string.IsNullOrWhiteSpace(sessionId) || snapshot == null) return;
         if (string.IsNullOrEmpty(snapshot.FullHash) && !string.IsNullOrEmpty(snapshot.PrefixHash))
             snapshot.FullHash = snapshot.PrefixHash;
         if (string.IsNullOrEmpty(snapshot.PrefixHash) && !string.IsNullOrEmpty(snapshot.FullHash))
             snapshot.PrefixHash = snapshot.FullHash;
         Store[sessionId.Trim()] = snapshot;
     }

     public static LastSentPromptSnapshot? Get(string? sessionId)
     {
         if (string.IsNullOrWhiteSpace(sessionId)) return null;
         return Store.TryGetValue(sessionId.Trim(), out var s) ? s : null;
     }

     /// <summary>
     /// need09-02：getPromptDiagnostics 双哈希对比（stableHash + fullHash 各自与预览对比）。
     /// Bridge 层冻结，暂由调用方（编排器/诊断）直接调本方法获取两者对比。
     /// </summary>
     public static PromptHashComparison CompareWithPreview(string? sessionId, string? previewStableHash, string? previewFullHash)
     {
         var last = Get(sessionId);
         if (last == null) return new PromptHashComparison(false, false, false);
         return new PromptHashComparison(
             true,
             string.Equals(last.StableHash ?? string.Empty, previewStableHash ?? string.Empty, StringComparison.Ordinal),
             string.Equals((string.IsNullOrEmpty(last.FullHash) ? last.PrefixHash : last.FullHash) ?? string.Empty, previewFullHash ?? string.Empty, StringComparison.Ordinal));
     }

     public static void ClearForTests() => Store.Clear();
 }
