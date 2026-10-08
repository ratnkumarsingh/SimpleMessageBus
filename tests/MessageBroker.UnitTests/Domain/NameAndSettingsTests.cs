using MessageBroker.Domain;

namespace MessageBroker.UnitTests.Domain;

/// <summary>U02 — names.</summary>
public class NameRulesTests
{
    [Theory]
    [InlineData("PaymentProcessed")]
    [InlineData("payments.v1_sub-B")]
    [InlineData("a")]
    public void Valid_names(string name) => Assert.True(NameRules.IsValid(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/name")]
    [InlineData("ünïcode")]
    public void Invalid_names(string? name) => Assert.False(NameRules.IsValid(name));

    [Fact]
    public void Length_limit_is_100()
    {
        Assert.True(NameRules.IsValid(new string('a', 100)));
        Assert.False(NameRules.IsValid(new string('a', 101)));
    }

    [Fact]
    public void Validate_throws_with_field_name()
    {
        var ex = Assert.Throws<BrokerValidationException>(() => NameRules.Validate("bad name", "name"));
        Assert.Equal("name", Assert.Single(ex.Errors).Field);
    }
}

/// <summary>U03 — subscription settings.</summary>
public class SubscriptionSettingsValidatorTests
{
    private static SubscriptionSettings Webhook(string? url = "https://b.internal/hook", int timeout = 30, int lockSeconds = 60) =>
        new(DeliveryMode.Webhook, url, timeout, 8, 4, lockSeconds, 30, 900, null);

    private static SubscriptionSettings Pull() => new(DeliveryMode.Pull, null, 30, 8, 4, 60, 30, 900, null);

    private static IEnumerable<string> Fields(SubscriptionSettings s, bool requireHttps = true) =>
        SubscriptionSettingsValidator.Validate(s, requireHttps).Select(e => e.Field);

    [Fact]
    public void Defaults_are_valid()
    {
        Assert.Empty(SubscriptionSettingsValidator.Validate(Webhook()));
        Assert.Empty(SubscriptionSettingsValidator.Validate(Pull()));
        Assert.Empty(SubscriptionSettingsValidator.Validate(Pull() with { DeliveryMode = DeliveryMode.SignalR }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a url")]
    [InlineData("http://b.internal/hook")]
    [InlineData("ftp://b.internal/hook")]
    public void Webhook_requires_https_url(string? url) => Assert.Contains("webhookUrl", Fields(Webhook(url)));

    [Fact]
    public void Http_allowed_only_when_https_not_required()
    {
        Assert.DoesNotContain("webhookUrl", Fields(Webhook("http://localhost:5000/hook"), requireHttps: false));
        Assert.Contains("webhookUrl", Fields(Webhook("ftp://localhost/hook"), requireHttps: false));
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(61, 60)]
    [InlineData(0, 60)]
    public void Webhook_timeout_must_be_shorter_than_lock(int timeout, int lockSeconds) =>
        Assert.Contains("webhookTimeoutSeconds", Fields(Webhook(timeout: timeout, lockSeconds: lockSeconds)));

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void Lock_between_1_and_600(int lockSeconds) =>
        Assert.Contains("lockDurationSeconds", Fields(Pull() with { LockDurationSeconds = lockSeconds }));

    [Fact]
    public void Lock_600_is_allowed() => Assert.Empty(Fields(Pull() with { LockDurationSeconds = 600 }));

    [Fact]
    public void MaxAttempts_at_least_1() => Assert.Contains("maxAttempts", Fields(Pull() with { MaxAttempts = 0 }));

    [Fact]
    public void Retry_base_must_not_exceed_max() =>
        Assert.Contains("retryMaxDelaySeconds", Fields(Pull() with { RetryBaseDelaySeconds = 100, RetryMaxDelaySeconds = 50 }));

    [Fact]
    public void Ttl_must_be_positive_when_set() => Assert.Contains("ttlSeconds", Fields(Pull() with { TtlSeconds = 0 }));

    [Theory]
    [InlineData(DeliveryMode.Pull)]
    [InlineData(DeliveryMode.SignalR)]
    public void Non_webhook_modes_must_not_have_url(DeliveryMode mode) =>
        Assert.Contains("webhookUrl", Fields(Pull() with { DeliveryMode = mode, WebhookUrl = "https://x/y" }));
}
