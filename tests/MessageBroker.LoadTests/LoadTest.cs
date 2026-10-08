using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using MessageBroker.Application.Security;
using MessageBroker.Contracts.Client;
using Microsoft.Extensions.Configuration;
using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

namespace MessageBroker.LoadTests;

/// <summary>
/// L01 — publishes at a fixed rate and checks p95 publish latency, while a pull consumer drains the
/// subscription so the broker is delivering at the same time.
/// <code>
///   dotnet run -c Release --project tests/MessageBroker.LoadTests                               2-minute smoke, self-hosted broker
///   dotnet run -c Release --project tests/MessageBroker.LoadTests -- --DurationSeconds 1800     the 30-minute report
///   ... -- --BrokerUrl http://broker:5080 --AdminKey mbk_...                                     against a running broker
/// </code>
/// Other options: --Rate (100), --P95Ms (100), --ReportFolder (artifacts/load-report).
/// Exit code 0 when the thresholds hold, 1 otherwise.
/// </summary>
public static class LoadTest
{
    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(10);

    public static async Task<int> Main(string[] args)
    {
        var config = new ConfigurationBuilder().AddCommandLine(args).Build();
        var durationSeconds = config.GetValue("DurationSeconds", 120);
        var rate = config.GetValue("Rate", 100);
        var p95LimitMs = config.GetValue("P95Ms", 100.0);
        var deliveryP95LimitMs = config.GetValue("DeliveryP95Ms", 1000.0);
        var reportFolder = Path.GetFullPath(config.GetValue("ReportFolder", "artifacts/load-report")!);

        await using var broker = config["BrokerUrl"] is { Length: > 0 } url
            ? BrokerUnderTest.Existing(new Uri(url), config["AdminKey"] ?? throw new InvalidOperationException("--AdminKey is required with --BrokerUrl."))
            : await BrokerUnderTest.StartAsync();

        Console.WriteLine($"Broker {broker.BaseAddress}; {rate} msg/s for {durationSeconds} s; thresholds: publish p95 < {p95LimitMs} ms, publish-to-receive p95 < {deliveryP95LimitMs} ms");
        var topology = await LoadTopology.CreateAsync(broker.BaseAddress, broker.AdminKey);

        using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 200 })
        {
            BaseAddress = broker.BaseAddress,
            Timeout = TimeSpan.FromSeconds(10),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(ApiKeys.Scheme, topology.PublisherKey);
        var path = $"api/v1/topics/{topology.TopicName}/messages";

        using var stopConsumer = new CancellationTokenSource();
        var consumed = 0L;
        // Publish-to-receive time (spec section 15: under 1 s at normal load), measured after the warm-up.
        var deliveryLatencies = new ConcurrentQueue<double>();
        var measureFrom = DateTime.UtcNow + WarmUp;
        var consumer = Task.Run(async () =>
        {
            using var client = new HttpClient { BaseAddress = broker.BaseAddress };
            var receiver = BrokerClient.Create(client, topology.ConsumerKey);
            while (!stopConsumer.IsCancellationRequested)
            {
                try
                {
                    var batch = await receiver.ReceiveAsync(topology.SubscriptionId, maxMessages: 32, waitSeconds: 1, stopConsumer.Token);
                    var receivedAt = DateTime.UtcNow;
                    if (receivedAt >= measureFrom)
                        foreach (var delivery in batch)
                            deliveryLatencies.Enqueue((receivedAt - delivery.Message.CreatedAt).TotalMilliseconds);
                    await Task.WhenAll(batch.Select(d => receiver.AckAsync(d, stopConsumer.Token)));
                    Interlocked.Add(ref consumed, batch.Count);
                }
                catch (OperationCanceledException) when (stopConsumer.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Consumer: {ex.Message}");
                    await Task.Delay(200);
                }
            }
        });

        var scenario = Scenario.Create("publish", async context =>
            {
                var body = $$$"""
                    {"messageType":"PaymentProcessed.v1","correlationId":"LOAD-{{{context.InvocationNumber}}}",
                     "properties":{"tenant":"in-01"},
                     "payload":{"paymentId":"PAY-{{{context.InvocationNumber}}}","amount":1500.00,"currency":"INR"}}
                    """;
                using var request = new HttpRequestMessage(HttpMethod.Post, path)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                // Unique per request: NBomber restarts InvocationNumber after the warm-up.
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
                try
                {
                    using var response = await http.SendAsync(request);
                    var status = ((int)response.StatusCode).ToString();
                    return response.IsSuccessStatusCode
                        ? Response.Ok(statusCode: status, sizeBytes: body.Length)
                        : Response.Fail(statusCode: status, message: await response.Content.ReadAsStringAsync());
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    return Response.Fail(statusCode: "transport", message: ex.Message);
                }
            })
            .WithWarmUpDuration(WarmUp)
            .WithLoadSimulations(Simulation.Inject(rate: rate, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(durationSeconds)));

        var result = NBomberRunner
            .RegisterScenarios(scenario)
            .WithTestSuite("MessageBroker")
            .WithTestName("L01-publish")
            .WithReportFolder(reportFolder)
            .WithReportFormats(ReportFormat.Html, ReportFormat.Md)
            .Run();

        await stopConsumer.CancelAsync();
        await consumer;

        var stats = result.ScenarioStats.Get("publish");
        var p95 = stats.Ok.Latency.Percent95;
        var failed = stats.Fail.Request.Count;
        var achievedRps = stats.Ok.Request.RPS;
        Console.WriteLine();
        Console.WriteLine($"L01 publish: ok {stats.Ok.Request.Count}, failed {failed}, {achievedRps:F1} msg/s, " +
            $"p50 {stats.Ok.Latency.Percent50:F1} ms, p95 {p95:F1} ms, p99 {stats.Ok.Latency.Percent99:F1} ms; consumed {consumed}");
        var sorted = deliveryLatencies.Order().ToArray();
        var deliveryP95 = sorted.Length == 0 ? double.NaN : sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
        Console.WriteLine($"L01 publish-to-receive (pull): {sorted.Length} samples, p95 {deliveryP95:F1} ms, max {(sorted.Length == 0 ? 0 : sorted[^1]):F1} ms");
        Console.WriteLine($"Reports: {reportFolder}");

        var problems = new List<string>();
        if (p95 >= p95LimitMs)
            problems.Add($"publish p95 {p95:F1} ms is not below {p95LimitMs} ms");
        if (!(deliveryP95 < deliveryP95LimitMs))
            problems.Add($"publish-to-receive p95 {deliveryP95:F1} ms is not below {deliveryP95LimitMs} ms");
        if (failed > 0)
            problems.Add($"{failed} publishes failed");
        if (achievedRps < rate * 0.95)
            problems.Add($"only {achievedRps:F1} msg/s of the {rate} msg/s target were published");
        foreach (var problem in problems)
            Console.WriteLine($"FAIL: {problem}");
        Console.WriteLine(problems.Count == 0 ? "L01 PASSED" : "L01 FAILED");
        return problems.Count == 0 ? 0 : 1;
    }
}
