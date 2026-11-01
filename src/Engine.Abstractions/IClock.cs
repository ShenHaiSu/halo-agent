namespace HaloAgent.Engine.Abstractions;

/// <summary>P14：可注入时钟（UTC ms），供 AgentSession.Record 打戳用，便于 FakeClock 单测单调 guard。</summary>
public interface IClock
{
    long UtcNowMs();
}

/// <summary>生产时钟：DateTimeOffset.UtcNow。</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public long UtcNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
