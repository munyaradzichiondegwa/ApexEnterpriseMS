using System.Net;
using ApexERP.API.Services;
using Xunit;

namespace ApexERP.Tests;

public sealed class TenantLifecycleDispatcherPolicyTests
{
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, 0, true)]
    [InlineData(HttpStatusCode.BadGateway, 7, true)]
    [InlineData(HttpStatusCode.TooManyRequests, 7, true)]
    [InlineData(HttpStatusCode.RequestTimeout, 7, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 8, false)]
    [InlineData(HttpStatusCode.BadRequest, 0, false)]
    [InlineData(HttpStatusCode.OK, 0, false)]
    public void Retry_policy_retries_only_transient_responses_with_attempts_remaining(
        HttpStatusCode statusCode, int attempts, bool expected)
    {
        Assert.Equal(expected, TenantLifecycleDispatcher.ShouldRetry(statusCode, attempts));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    [InlineData(9, false)]
    public void Dispatch_attempt_budget_is_bounded(int attempts, bool expected)
    {
        Assert.Equal(expected, TenantLifecycleDispatcher.HasAttemptsRemaining(attempts));
    }

    [Fact]
    public void Worker_signature_is_deterministic_and_payload_sensitive()
    {
        const string secret = "test-worker-secret";
        const string payload = "1700000000\n{\"jobId\":\"job-1\"}";

        var first = TenantLifecycleDispatcher.Sign(secret, payload);
        var second = TenantLifecycleDispatcher.Sign(secret, payload);
        var changedPayload = TenantLifecycleDispatcher.Sign(secret, payload + " ");

        Assert.Equal(first, second);
        Assert.NotEqual(first, changedPayload);
        Assert.Equal(64, first.Length);
    }
}
