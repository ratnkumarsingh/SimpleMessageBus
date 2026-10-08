using System.Text.Json;
using MessageBroker.Domain;

namespace MessageBroker.UnitTests.Domain;

/// <summary>U01 — envelope validation.</summary>
public class EnvelopeValidatorTests
{
    private static readonly BrokerLimits Limits = new();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static PublishEnvelope Valid(
        string? messageType = "PaymentProcessed.v1",
        string? correlationId = "PAY-1",
        string? idempotencyKey = "outbox-1",
        int? ttl = 60,
        JsonElement? properties = null,
        JsonElement? payload = null) =>
        new(messageType, correlationId, idempotencyKey, ttl,
            properties ?? Json("""{"tenant":"in-01"}"""),
            payload ?? Json("""{"paymentId":"PAY-1","amount":1500.00}"""));

    private static IReadOnlyList<ValidationError> Errors(PublishEnvelope e) =>
        Assert.Throws<BrokerValidationException>(() => EnvelopeValidator.Validate(e, Limits)).Errors;

    [Fact]
    public void Valid_envelope_returns_raw_json()
    {
        var result = EnvelopeValidator.Validate(Valid(), Limits);

        Assert.Equal("PaymentProcessed.v1", result.MessageType);
        Assert.Equal("""{"paymentId":"PAY-1","amount":1500.00}""", result.PayloadJson);
        Assert.Equal("""{"tenant":"in-01"}""", result.PropertiesJson);
    }

    [Fact]
    public void Optional_fields_may_be_absent()
    {
        var e = new PublishEnvelope("T.v1", null, null, null, null, Json("{}"));
        var result = EnvelopeValidator.Validate(e, Limits);
        Assert.Null(result.CorrelationId);
        Assert.Null(result.PropertiesJson);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MessageType_is_required(string? messageType) =>
        Assert.Contains(Errors(Valid(messageType: messageType)), e => e.Field == "messageType");

    [Fact]
    public void MessageType_max_200()
    {
        EnvelopeValidator.Validate(Valid(messageType: new string('a', 200)), Limits);
        Assert.Contains(Errors(Valid(messageType: new string('a', 201))), e => e.Field == "messageType");
    }

    [Fact]
    public void CorrelationId_max_100()
    {
        EnvelopeValidator.Validate(Valid(correlationId: new string('c', 100)), Limits);
        Assert.Contains(Errors(Valid(correlationId: new string('c', 101))), e => e.Field == "correlationId");
    }

    [Fact]
    public void IdempotencyKey_max_100()
    {
        EnvelopeValidator.Validate(Valid(idempotencyKey: new string('k', 100)), Limits);
        Assert.Contains(Errors(Valid(idempotencyKey: new string('k', 101))), e => e.Field == "Idempotency-Key");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Ttl_must_be_positive(int ttl) =>
        Assert.Contains(Errors(Valid(ttl: ttl)), e => e.Field == "ttlSeconds");

    [Theory]
    [InlineData("null")]
    public void Payload_is_required(string payload)
    {
        var e = Valid() with { Payload = Json(payload) };
        Assert.Contains(Errors(e), x => x.Field == "payload");
        Assert.Contains(Errors(Valid() with { Payload = null }), x => x.Field == "payload");
    }

    [Fact]
    public void Payload_over_limit_is_PayloadTooLarge()
    {
        var big = Json($$"""{"data":"{{new string('x', 262_144)}}"}""");
        var ex = Assert.Throws<BrokerException>(() => EnvelopeValidator.Validate(Valid(payload: big), Limits));
        Assert.Equal(BrokerErrorKind.PayloadTooLarge, ex.Kind);
    }

    [Fact]
    public void Payload_exactly_at_limit_is_accepted()
    {
        // {"d":"..."} has 8 bytes of framing
        var exact = Json($$"""{"d":"{{new string('x', 262_144 - 8)}}"}""");
        EnvelopeValidator.Validate(Valid(payload: exact), Limits);
    }

    [Theory]
    [InlineData("""{"n":1}""")]
    [InlineData("""{"a":{"b":"c"}}""")]
    [InlineData("""["x"]""")]
    public void Properties_must_be_object_of_strings(string properties) =>
        Assert.Contains(Errors(Valid(properties: Json(properties))), e => e.Field == "properties");

    [Fact]
    public void Properties_over_4KB_rejected()
    {
        var big = Json($$"""{"p":"{{new string('x', 4_100)}}"}""");
        Assert.Contains(Errors(Valid(properties: big)), e => e.Field == "properties");
    }

    [Fact]
    public void All_errors_reported_together()
    {
        var e = new PublishEnvelope(null, new string('c', 101), null, -1, null, null);
        var fields = Errors(e).Select(x => x.Field).ToHashSet();
        Assert.Superset(new HashSet<string> { "messageType", "correlationId", "ttlSeconds", "payload" }, fields);
    }
}
