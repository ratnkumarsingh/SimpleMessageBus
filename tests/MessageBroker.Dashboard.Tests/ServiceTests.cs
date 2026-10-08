using System.Net;
using System.Security.Claims;
using MessageBroker.Dashboard.Components.Pages;
using MessageBroker.Dashboard.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MessageBroker.Dashboard.Tests;

/// <summary>V01: sign-in. V08b: the live feed's polling fallback. Plus the refresh throttle.</summary>
public sealed class ServiceTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://broker.test/") };
    }

    private static AdminKeyValidator Validator(HttpMessageHandler handler) =>
        new(new BrokerClientFactory(new StubHttpClientFactory(handler)), NullLogger<AdminKeyValidator>.Instance);

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "{}") =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Theory(DisplayName = "V01 Sign-in accepts an Admin key and tells apart non-admin, invalid and unreachable")]
    [InlineData(HttpStatusCode.OK, AdminKeyValidator.Result.Valid)]
    [InlineData(HttpStatusCode.Forbidden, AdminKeyValidator.Result.NotAdmin)]
    [InlineData(HttpStatusCode.Unauthorized, AdminKeyValidator.Result.Invalid)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AdminKeyValidator.Result.BrokerUnavailable)]
    public async Task V01_ValidateKey(HttpStatusCode status, AdminKeyValidator.Result expected)
    {
        var handler = new StubHandler(_ => Status(status));
        Assert.Equal(expected, await Validator(handler).ValidateAsync("mbk_abc123def456_secret", CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/admin/overview", request.RequestUri!.AbsolutePath);
        Assert.Equal("ApiKey mbk_abc123def456_secret", request.Headers.Authorization!.ToString());
    }

    [Fact(DisplayName = "V01b An unreachable broker is reported, not thrown")]
    public async Task V01b_Unreachable()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("refused"));
        Assert.Equal(AdminKeyValidator.Result.BrokerUnavailable, await Validator(handler).ValidateAsync("mbk_x_y", CancellationToken.None));
    }

    [Theory(DisplayName = "V01c Only local return URLs are followed after sign-in")]
    [InlineData("/messages?status=DeadLettered", "/messages?status=DeadLettered")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("/login?returnUrl=/x", "/")]
    public void V01c_SafeReturnUrl(string? input, string expected) => Assert.Equal(expected, Login.SafeReturnUrl(input));

    [Fact(DisplayName = "V01d Key prefixes are what the dashboard shows, never the secret part")]
    public void V01d_Prefix()
    {
        Assert.Equal("mbk_abc123def456", DashboardClaims.PrefixOf("mbk_abc123def456_s3cr3t"));
        Assert.Equal("key", DashboardClaims.PrefixOf("garbage"));
    }

    [Fact(DisplayName = "V01e Every page needs sign-in; the sign-in page itself is open")]
    public async Task V01e_PagesRequireSignIn()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var path in new[] { "/", "/messages", "/deadletters", "/topology", $"/messages/{Guid.NewGuid()}" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("http://localhost/login?ReturnUrl=", response.Headers.Location!.ToString());
        }

        var login = await client.GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var html = await login.Content.ReadAsStringAsync();
        Assert.Contains("Admin API key", html);
        Assert.Contains("__RequestVerificationToken", html);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/logout", null)).StatusCode); // antiforgery required
    }

    [Fact(DisplayName = "RefreshThrottle runs at most once per interval and still fires under a steady stream")]
    public async Task Throttle()
    {
        var time = new FakeTimeProvider();
        using var throttle = new RefreshThrottle(TimeSpan.FromSeconds(1), time);
        var runs = 0;
        Task Refresh() { Interlocked.Increment(ref runs); return Task.CompletedTask; }

        for (var i = 0; i < 10; i++)
            throttle.Request(Refresh);
        Assert.Equal(0, runs);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => runs == 1);

        throttle.Request(Refresh);
        time.Advance(TimeSpan.FromMilliseconds(500));
        throttle.Request(Refresh);
        time.Advance(TimeSpan.FromMilliseconds(500));
        await WaitUntilAsync(() => runs == 2);
        Assert.Equal(2, runs);
    }

    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(DashboardClaims.ApiKey, "mbk_abc_def")], "test"))));
    }

    [Fact(DisplayName = "V08b With the admin hub unreachable the feed goes Offline and polls on the fallback interval")]
    public async Task V08b_OfflinePolling()
    {
        var time = new FakeTimeProvider();
        // Port 9 (discard) refuses connections at once on a developer machine.
        var options = Options.Create(new DashboardOptions { BrokerUrl = new Uri("http://127.0.0.1:9/"), FallbackPollSeconds = 10 });
        await using var feed = new BrokerLiveFeed(options, new SignedIn(), time, NullLogger<BrokerLiveFeed>.Instance);
        var states = new List<LiveState>();
        var polls = 0;
        feed.StateChanged += () => states.Add(feed.State);
        feed.OverviewChanged += () => Interlocked.Increment(ref polls);

        await feed.StartAsync();
        await WaitUntilAsync(() => feed.State == LiveState.Offline, seconds: 20);
        Assert.Equal(LiveState.Offline, states[0]);

        await WaitUntilAsync(() =>
        {
            time.Advance(TimeSpan.FromSeconds(10));
            return polls > 0;
        }, seconds: 20);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out.");
            await Task.Delay(20);
        }
    }
}
