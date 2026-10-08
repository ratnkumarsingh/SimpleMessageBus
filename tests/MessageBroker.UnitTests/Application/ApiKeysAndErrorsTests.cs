using MessageBroker.Application.Errors;
using MessageBroker.Application.Security;
using MessageBroker.Contracts;
using MessageBroker.Domain;
using MessageBroker.Infrastructure.Data;

namespace MessageBroker.UnitTests.Application;

/// <summary>U05 — API key generation and verification.</summary>
public class ApiKeysTests
{
    [Fact]
    public void Generated_key_has_prefix_format_and_round_trips()
    {
        var issued = ApiKeys.Generate();

        Assert.Matches("^mbk_[a-z2-9]{12}$", issued.Prefix);
        Assert.Matches("^mbk_[a-z2-9]{12}_[A-Za-z0-9_-]{43}$", issued.Key);
        Assert.True(ApiKeys.TryGetPrefix(issued.Key, out var prefix));
        Assert.Equal(issued.Prefix, prefix);
        Assert.Equal(32, issued.Hash.Length);
        Assert.True(ApiKeys.Verify(issued.Key, issued.Hash));
    }

    [Fact]
    public void Wrong_key_fails_verification()
    {
        var issued = ApiKeys.Generate();
        var other = ApiKeys.Generate();

        Assert.False(ApiKeys.Verify(other.Key, issued.Hash));
        Assert.False(ApiKeys.Verify(issued.Key + "x", issued.Hash));
        Assert.False(ApiKeys.Verify(issued.Prefix + "_" + new string('A', 43), issued.Hash));
    }

    [Fact]
    public void Keys_are_unique()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => ApiKeys.Generate()).ToList();
        Assert.Equal(200, keys.Select(k => k.Key).Distinct().Count());
        Assert.Equal(200, keys.Select(k => k.Prefix).Distinct().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mbk_short")]
    [InlineData("pk_live_7f3a0000000000000000000000")]
    [InlineData("mbk_abcdefghijkmXsecretsecretsecret")]
    public void Malformed_keys_have_no_prefix(string? key) => Assert.False(ApiKeys.TryGetPrefix(key, out _));

    [Fact]
    public void Webhook_secrets_are_random_base64url()
    {
        var a = ApiKeys.NewWebhookSecret();
        Assert.Matches("^[A-Za-z0-9_-]{43}$", a);
        Assert.NotEqual(a, ApiKeys.NewWebhookSecret());
    }
}

/// <summary>U09 — SQL error mapping and problem descriptions.</summary>
public class ErrorMappingTests
{
    [Theory]
    [InlineData(50400, BrokerErrorKind.Validation, 400, ProblemTypes.Validation)]
    [InlineData(50403, BrokerErrorKind.Forbidden, 403, ProblemTypes.Forbidden)]
    [InlineData(50404, BrokerErrorKind.NotFound, 404, ProblemTypes.NotFound)]
    [InlineData(50409, BrokerErrorKind.Conflict, 409, ProblemTypes.Conflict)]
    [InlineData(50410, BrokerErrorKind.LeaseLost, 410, ProblemTypes.LeaseLost)]
    [InlineData(2627, BrokerErrorKind.Conflict, 409, ProblemTypes.Conflict)]
    [InlineData(2601, BrokerErrorKind.Conflict, 409, ProblemTypes.Conflict)]
    public void Procedure_errors_map_to_status_and_type(int number, BrokerErrorKind kind, int status, string type)
    {
        var mapped = SqlErrorMapper.Map(number, "message from the procedure");

        Assert.NotNull(mapped);
        Assert.Equal(kind, mapped.Kind);
        var problem = BrokerProblems.Describe(mapped.Kind);
        Assert.Equal(status, problem.Status);
        Assert.Equal(type, problem.Type);
        if (number >= 50000)
            Assert.Equal("message from the procedure", mapped.Message);
    }

    [Theory]
    [InlineData(-2)]     // timeout
    [InlineData(53)]     // server not found
    [InlineData(1205)]   // deadlock victim
    [InlineData(4060)]   // database unavailable
    [InlineData(10054)]  // connection reset
    public void Connection_failures_are_503(int number)
    {
        var mapped = SqlErrorMapper.Map(number, "network");
        Assert.Equal(BrokerErrorKind.Unavailable, mapped!.Kind);
        Assert.Equal(503, BrokerProblems.Describe(mapped.Kind).Status);
        Assert.Equal(ProblemTypes.Unavailable, BrokerProblems.Describe(mapped.Kind).Type);
    }

    [Theory]
    [InlineData(208)]    // invalid object name
    [InlineData(8134)]   // divide by zero
    [InlineData(50000)]  // plain RAISERROR
    public void Unknown_errors_are_left_for_the_500_handler(int number) => Assert.Null(SqlErrorMapper.Map(number, "boom"));

    [Fact]
    public void Every_error_kind_has_a_problem_description()
    {
        foreach (var kind in Enum.GetValues<BrokerErrorKind>())
        {
            var problem = BrokerProblems.Describe(kind);
            Assert.InRange(problem.Status, 400, 599);
            Assert.StartsWith("urn:message-broker:problem:", problem.Type);
            Assert.Equal(problem.Type, BrokerProblems.TypeForStatus(problem.Status));
        }
        Assert.Equal(413, BrokerProblems.Describe(BrokerErrorKind.PayloadTooLarge).Status);
        Assert.Equal(401, BrokerProblems.Describe(BrokerErrorKind.Unauthorized).Status);
    }
}
