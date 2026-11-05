using System.Text.Json;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Storage.Sessions;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Llm;

/// <summary>Adapters_TsMs_RoundTrip：Stored→Chat→Stored 不丢；LLM 序列化体无 ts 字段。</summary>
public sealed class MessageAdaptersTsMsTests
{
    [Fact]
    public void Stored_Chat_Stored_RoundTrip_Preserves_TsMs()
    {
        var stored = StoredMessage.User("hi", tsMs: 1700000000001L);
        var chat = MessageAdapters.ToChatMessage(stored);
        Assert.Equal(1700000000001L, chat.TsMs);
        var back = MessageAdapters.ToStoredMessage(chat);
        Assert.Equal(1700000000001L, back.TsMs);
        Assert.Equal("hi", back.Content);
    }

    [Fact]
    public void Parts_Branch_Preserves_TsMs()
    {
        var chat = new ChatMessage
        {
            RoleWire = "user",
            ContentParts = new List<ChatContentPart>
            {
                ChatContentPart.TextPart("hello"),
                ChatContentPart.ImagePart("data:image/png;base64,AAA"),
            },
            TsMs = 1700000000002L,
        };
        var stored = MessageAdapters.ToStoredMessage(chat);
        Assert.Equal(1700000000002L, stored.TsMs);
        var back = MessageAdapters.ToChatMessage(stored);
        Assert.Equal(1700000000002L, back.TsMs);
        Assert.True(back.HasImages);
    }

    [Fact]
    public void Llm_Request_Body_Contains_No_Ts()
    {
        var chat = ChatMessage.User("hi", tsMs: 1700000000003L);
        var json = JsonSerializer.Serialize(chat);
        Assert.DoesNotContain("ts_ms", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TsMs", json, StringComparison.Ordinal);
        Assert.DoesNotContain("tsMs", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Stored_Serialization_Contains_TsMs_And_Old_Json_Reads_As_Zero()
    {
        var stored = StoredMessage.User("hi", tsMs: 1700000000004L);
        var json = JsonSerializer.Serialize(stored, HaloAgent.Engine.Storage.Config.StorageJson.Compact);
        Assert.Contains("ts_ms", json, StringComparison.Ordinal);

        // 旧文件（无 ts_ms）反序列化为 0
        var old = JsonSerializer.Deserialize<StoredMessage>(
            "{\"role\":\"user\",\"content\":\"hi\"}",
            HaloAgent.Engine.Storage.Config.StorageJson.Compact);
        Assert.NotNull(old);
        Assert.Equal(0, old!.TsMs);
    }
}
