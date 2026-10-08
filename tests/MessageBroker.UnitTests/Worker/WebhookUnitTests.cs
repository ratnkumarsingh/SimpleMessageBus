using System.Security.Cryptography;
using System.Text;
using MessageBroker.Application;
using MessageBroker.Contracts.Webhooks;
using MessageBroker.Worker.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MessageBroker.UnitTests.Worker;

/// <summary>U04 — HMAC signing and verification.</summary>
public class WebhookSignatureTests
{
    private const string Body = """{"deliveryId":81231,"message":{"payload":{"amount":1500.00}}}""";
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 5, 12, 0, TimeSpan.Zero);
    private static readonly string Timestamp = WebhookSignature.Timestamp(Now);

    /// <summary>The verification code from spec section 9, verbatim.</summary>
    private static bool SpecSample(string secret, string timestamp, string body, string header)
    {
        var data = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data);
        var expected = "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(header));
    }

    [Fact]
    public void Signature_matches_the_spec_sample_and_format()
    {
        var signature = WebhookSignature.Sign("secret-1", Timestamp, Body);
        Assert.Matches("^sha256=[0-9a-f]{64}$", signature);
        Assert.True(SpecSample("secret-1", Timestamp, Body, signature));
        Assert.Equal("1791436320", Timestamp);
    }

    [Fact]
    public void Known_vector()
    {
        // HMAC-SHA256("key", "1.{}") computed independently.
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData("key"u8, "1.{}"u8));
        Assert.Equal(expected, WebhookSignature.Sign("key", "1", "{}"));
    }

    [Fact]
    public void Dual_signature_header_lists_new_then_old()
    {
        var header = WebhookSignature.CreateHeader(Timestamp, Body, "new", "old");
        Assert.Equal($"{WebhookSignature.Sign("new", Timestamp, Body)},{WebhookSignature.Sign("old", Timestamp, Body)}", header);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("old")]
    public void Verifier_accepts_either_secret_during_rotation(string receiverSecret)
    {
        var header = WebhookSignature.CreateHeader(Timestamp, Body, "new", "old");
        Assert.True(WebhookSignature.Verify(header, Timestamp, Body, Now, [receiverSecret]));
    }

    [Fact]
    public void Verifier_rejects_tampering_wrong_secret_and_malformed_input()
    {
        var header = WebhookSignature.CreateHeader(Timestamp, Body, "secret-1");
        Assert.True(WebhookSignature.Verify(header, Timestamp, Body, Now, ["secret-1"]));

        Assert.False(WebhookSignature.Verify(header, Timestamp, Body.Replace("1500.00", "9500.00"), Now, ["secret-1"]));
        Assert.False(WebhookSignature.Verify(header, Timestamp, Body, Now, ["secret-2"]));
        Assert.False(WebhookSignature.Verify(header, (long.Parse(Timestamp) + 1).ToString(), Body, Now, ["secret-1"]));
        Assert.False(WebhookSignature.Verify(header.ToUpperInvariant(), Timestamp, Body, Now, ["secret-1"]));
        Assert.False(WebhookSignature.Verify(null, Timestamp, Body, Now, ["secret-1"]));
        Assert.False(WebhookSignature.Verify(header, "not-a-number", Body, Now, ["secret-1"]));
        Assert.False(WebhookSignature.Verify(header, "-5", Body, Now, ["secret-1"]));
        Assert.False(WebhookSignature.Verify(header, Timestamp, Body, Now, []));
    }

    [Theory]
    [InlineData(-299, true)]
    [InlineData(-300, true)]
    [InlineData(-301, false)]
    [InlineData(299, true)]
    [InlineData(301, false)]
    public void Verifier_enforces_the_five_minute_window(int signedSecondsFromNow, bool valid)
    {
        var timestamp = WebhookSignature.Timestamp(Now.AddSeconds(signedSecondsFromNow));
        var header = WebhookSignature.Sign("s", timestamp, Body);
        Assert.Equal(valid, WebhookSignature.Verify(header, timestamp, Body, Now, ["s"]));
    }
}

/// <summary>U06 — circuit breaker.</summary>
public class CircuitBreakerTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly CircuitBreakerRegistry _circuits;
    private readonly Guid _sub = Guid.NewGuid();

    public CircuitBreakerTests() =>
        _circuits = new CircuitBreakerRegistry(Options.Create(new BrokerOptions()), _time, NullLogger<CircuitBreakerRegistry>.Instance);

    private void Fail(int times)
    {
        for (var i = 0; i < times; i++)
            _circuits.RecordFailure(_sub);
    }

    [Fact]
    public void Four_failures_stay_closed_and_the_fifth_opens()
    {
        Fail(4);
        Assert.Equal(CircuitState.Closed, _circuits.GetState(_sub));
        Assert.Equal(int.MaxValue, _circuits.Allowance(_sub));

        Fail(1);
        Assert.Equal(CircuitState.Open, _circuits.GetState(_sub));
        Assert.Equal(0, _circuits.Allowance(_sub));
    }

    [Fact]
    public void A_success_resets_the_failure_count()
    {
        Fail(4);
        _circuits.RecordSuccess(_sub);
        Fail(4);
        Assert.Equal(CircuitState.Closed, _circuits.GetState(_sub));
    }

    [Fact]
    public void After_60_seconds_it_allows_exactly_one_trial()
    {
        Fail(5);
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, _circuits.Allowance(_sub));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(CircuitState.HalfOpen, _circuits.GetState(_sub));
        Assert.Equal(1, _circuits.Allowance(_sub));

        _circuits.OnAttemptStarted(_sub);
        Assert.Equal(0, _circuits.Allowance(_sub));
    }

    [Fact]
    public void Trial_success_closes_the_circuit()
    {
        Fail(5);
        _time.Advance(TimeSpan.FromSeconds(60));
        _circuits.OnAttemptStarted(_sub);
        _circuits.RecordSuccess(_sub);

        Assert.Equal(CircuitState.Closed, _circuits.GetState(_sub));
        Assert.Equal(int.MaxValue, _circuits.Allowance(_sub));
        Fail(4);
        Assert.Equal(CircuitState.Closed, _circuits.GetState(_sub));
    }

    [Fact]
    public void Trial_failure_reopens_for_another_full_interval()
    {
        Fail(5);
        _time.Advance(TimeSpan.FromSeconds(60));
        _circuits.OnAttemptStarted(_sub);
        _circuits.RecordFailure(_sub);

        Assert.Equal(CircuitState.Open, _circuits.GetState(_sub));
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(CircuitState.Open, _circuits.GetState(_sub));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(CircuitState.HalfOpen, _circuits.GetState(_sub));
    }

    [Fact]
    public void Late_failures_while_open_do_not_extend_the_interval()
    {
        Fail(5);
        _time.Advance(TimeSpan.FromSeconds(30));
        Fail(3); // calls that were already in flight
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(CircuitState.HalfOpen, _circuits.GetState(_sub));
    }

    [Fact]
    public void Circuits_are_per_subscription()
    {
        Fail(5);
        Assert.Equal(CircuitState.Closed, _circuits.GetState(Guid.NewGuid()));
    }

    [Fact]
    public void Threshold_and_interval_come_from_configuration()
    {
        var options = new BrokerOptions();
        options.Webhooks.CircuitFailureThreshold = 2;
        options.Webhooks.CircuitOpenSeconds = 10;
        var circuits = new CircuitBreakerRegistry(Options.Create(options), _time, NullLogger<CircuitBreakerRegistry>.Instance);

        circuits.RecordFailure(_sub);
        circuits.RecordFailure(_sub);
        Assert.Equal(CircuitState.Open, circuits.GetState(_sub));
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(CircuitState.HalfOpen, circuits.GetState(_sub));
    }
}

/// <summary>U07 — webhook response mapping.</summary>
public class WebhookResponseClassifierTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(204)]
    public void Success_statuses_ack(int status)
    {
        var result = WebhookResponseClassifier.FromStatus(status);
        Assert.Equal((WebhookDecision.Ack, status, (string?)null), (result.Decision, result.HttpStatusCode!.Value, result.ErrorCode));
        Assert.True(result.EndpointHealthy);
    }

    [Fact]
    public void Accepted_holds_the_lease()
    {
        var result = WebhookResponseClassifier.FromStatus(202);
        Assert.Equal(WebhookDecision.Hold, result.Decision);
        Assert.True(result.EndpointHealthy);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(503)]
    public void Other_statuses_nack_with_the_status_as_error_code(int status)
    {
        var result = WebhookResponseClassifier.FromStatus(status);
        Assert.Equal((WebhookDecision.Nack, status, $"Http{status}"), (result.Decision, result.HttpStatusCode!.Value, result.ErrorCode));
        Assert.False(result.EndpointHealthy);
        if (status is >= 300 and < 400)
            Assert.Contains("redirects are not followed", result.ErrorMessage);
    }

    [Fact]
    public void Timeout_and_connection_errors_nack()
    {
        var timeout = WebhookResponseClassifier.Timeout(30);
        Assert.Equal((WebhookDecision.Nack, "Timeout", (int?)null), (timeout.Decision, timeout.ErrorCode, timeout.HttpStatusCode));
        Assert.Contains("30 s", timeout.ErrorMessage);

        var refused = WebhookResponseClassifier.ConnectionError(new HttpRequestException("Connection refused"));
        Assert.Equal((WebhookDecision.Nack, "ConnectionError", "Connection refused"), (refused.Decision, refused.ErrorCode, refused.ErrorMessage));
        Assert.False(refused.EndpointHealthy);
    }
}
