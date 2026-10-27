using HaloAgent.Engine.Core.Llm;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Llm;

public class RetryPolicyTests
{
    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void Retryable_status_has_reason(int status)
    {
        Assert.NotNull(RetryPolicy.GetRetryReason(new EndpointStatusException(status, "x")));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public void Non_retryable_status_has_no_reason(int status)
    {
        Assert.Null(RetryPolicy.GetRetryReason(new EndpointStatusException(status, "x")));
    }

    [Fact]
    public void Cancelled_operation_has_no_reason()
    {
        Assert.Null(RetryPolicy.GetRetryReason(new OperationCanceledException()));
        Assert.Null(RetryPolicy.GetRetryReason(new TaskCanceledException()));
    }

    [Fact]
    public void Connection_failure_has_reason()
    {
        Assert.NotNull(RetryPolicy.GetRetryReason(new HttpRequestException("conn")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Backoff_stays_within_800_to_8200_ms(int attempt)
    {
        var backoff = RetryPolicy.Backoff(attempt);

        Assert.InRange(backoff.TotalMilliseconds, 800, 8200);
    }
}
