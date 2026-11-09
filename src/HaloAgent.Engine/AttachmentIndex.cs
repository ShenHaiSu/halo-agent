using System.Collections.Concurrent;
using HaloAgent.Engine.Abstractions;

namespace HaloAgent.Engine;

/// <summary>
/// Attachment truth: digest dedup with reference semantics.
/// Bytes stay in storage; history and events carry only the digest and path.
/// Stored paths are always relative so payloads stay portable across machines.
/// </summary>
public sealed class AttachmentIndex
{
    private readonly ConcurrentDictionary<string, AttachmentReference> _byDigest =
        new(StringComparer.Ordinal);

    public AttachmentReference Register(
        string digest,
        string sessionId,
        string fileName,
        long byteSize
    )
    {
        return Register(
            digest,
            sessionId,
            fileName,
            byteSize,
            mime: string.Empty,
            relPath: string.Empty
        );
    }

    public AttachmentReference Register(
        string digest,
        string sessionId,
        string fileName,
        long byteSize,
        string mime,
        string relPath
    )
    {
        string safeDigest = string.IsNullOrWhiteSpace(digest)
            ? Guid.NewGuid().ToString("N")
            : digest;
        string safeName = string.IsNullOrWhiteSpace(fileName) ? "attachment.bin" : fileName;
        string path = string.IsNullOrWhiteSpace(relPath)
            ? $"attachments/{safeDigest}-{safeName}"
            : relPath.TrimStart('/', '\\');
        var reference = new AttachmentReference(
            safeDigest,
            sessionId,
            safeName,
            Math.Max(0, byteSize),
            path,
            mime ?? string.Empty,
            path
        );
        return _byDigest.GetOrAdd(safeDigest, reference);
    }

    public bool TryLookup(string digest, out AttachmentReference? reference)
    {
        return _byDigest.TryGetValue(digest, out reference);
    }

    public int Count => _byDigest.Count;
}
