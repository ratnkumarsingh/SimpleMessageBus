using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MessageBroker.IntegrationTests.Infrastructure;

namespace MessageBroker.IntegrationTests.Samples;

/// <summary>
/// S13 — The ActiveBatch sample scripts, run the way a scheduler runs them: a PowerShell process calling the
/// broker on a real port, judged by its exit code (0 published, 1 refused, 2 broker unavailable).
/// </summary>
public sealed class ActiveBatchSampleTests(SqlServerFixture sql) : ApiTest(sql)
{
    private static readonly string ScriptFolder = Path.Combine(AppContext.BaseDirectory, "ActiveBatch");

    private sealed record Run(int ExitCode, string Output);

    private static async Task<Run> RunScriptAsync(string shell, string script, string apiKey, params string[] arguments)
    {
        var start = new ProcessStartInfo(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment = { ["BROKER_API_KEY"] = apiKey },
        };
        foreach (var argument in (string[])["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                     "-File", Path.Combine(ScriptFolder, script), .. arguments])
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        return new Run(process.ExitCode, await output + await error);
    }

    private static bool IsInstalled(string shell) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(dir => File.Exists(Path.Combine(dir, shell)));

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Theory(DisplayName = "S13 ActiveBatch scripts: Publish-BrokerEvent.ps1 publishes once per Idempotency-Key and exits 0, 1 when refused, 2 when the broker is unreachable; the example job notifies once per business date")]
    [InlineData("powershell.exe")] // Windows PowerShell 5.1, the usual ActiveBatch agent shell
    [InlineData("pwsh.exe")]       // PowerShell 7
    public async Task S13_PublishScriptExitCodes(string shell)
    {
        if (!IsInstalled(shell))
            return; // nothing to run this shell with on this machine

        var topic = await CreateTopicViaApiAsync();
        var (publisherId, publisherKey, _) = await CreateAppClientAsync();
        await GrantViaApiAsync(publisherId, "Topic", topic.TopicId, "Publish");

        // A second host of the same broker database, on a real port the script can call.
        var port = FreePort();
        await using var kestrel = new ApiFactory(Sql.ConnectionString);
        kestrel.UseKestrel(port);
        kestrel.StartServer();
        var brokerUrl = $"http://127.0.0.1:{port}/";

        Task<Run> PublishAsync(string key, string topicName, string payload = """{"orderId":"ORD-13"}""", string apiKey = "", params string[] extra) =>
            RunScriptAsync(shell, "Publish-BrokerEvent.ps1", apiKey is "" ? publisherKey : apiKey,
                ["-BrokerUrl", brokerUrl, "-Topic", topicName, "-MessageType", "OrderShipped.v1",
                 "-Payload", payload, "-IdempotencyKey", key, "-CorrelationId", "ORD-13", .. extra]);
        Task<int> MessagesAsync() => ScalarAsync<int>("SELECT COUNT(*) FROM broker.Messages");

        // Published, then the same key again (a job rerun) is a success that adds nothing.
        var first = await PublishAsync("job-S13-1", topic.Name);
        Assert.True(first.ExitCode == 0, first.Output);
        Assert.Contains("published message", first.Output);
        var again = await PublishAsync("job-S13-1", topic.Name);
        Assert.True(again.ExitCode == 0, again.Output);
        Assert.Contains("already published", again.Output);
        Assert.Equal(1, await MessagesAsync());
        var stored = Assert.Single(await QueryAsync<(string Key, string Type, string Correlation, string Payload)>(
            "SELECT IdempotencyKey, MessageType, CorrelationId, Payload FROM broker.Messages"));
        Assert.Equal(("job-S13-1", "OrderShipped.v1", "ORD-13"), (stored.Key, stored.Type, stored.Correlation));
        Assert.Contains("ORD-13", stored.Payload);

        // From a plain command line the JSON comes from a file, since quotes do not survive a Windows command line.
        var payloadFile = Path.Combine(Path.GetTempPath(), $"S13-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(payloadFile, "{\n  \"orderId\": \"ORD-13F\"\n}\n");
        try
        {
            var fromFile = await RunScriptAsync(shell, "Publish-BrokerEvent.ps1", publisherKey,
                "-BrokerUrl", brokerUrl, "-Topic", topic.Name, "-MessageType", "OrderShipped.v1",
                "-IdempotencyKey", "job-S13-file", "-PayloadFile", payloadFile);
            Assert.True(fromFile.ExitCode == 0, fromFile.Output);
            Assert.Contains("ORD-13F", await ScalarAsync<string>(
                "SELECT Payload FROM broker.Messages WHERE IdempotencyKey = N'job-S13-file'"));
        }
        finally
        {
            File.Delete(payloadFile);
        }
        var noFile = await RunScriptAsync(shell, "Publish-BrokerEvent.ps1", publisherKey,
            "-BrokerUrl", brokerUrl, "-Topic", topic.Name, "-MessageType", "OrderShipped.v1",
            "-IdempotencyKey", "job-S13-nofile", "-PayloadFile", payloadFile);
        Assert.True(noFile.ExitCode == 1, noFile.Output);
        Assert.Contains("was not found", noFile.Output);
        Assert.Equal(2, await MessagesAsync());

        // Refused: unknown topic (404), wrong key (401), bad JSON (caught before any call). Exit 1, nothing stored.
        var noTopic = await PublishAsync("job-S13-2", "no-such-topic");
        Assert.True(noTopic.ExitCode == 1, noTopic.Output);
        Assert.Contains("HTTP 404", noTopic.Output);
        var badKey = await PublishAsync("job-S13-3", topic.Name, apiKey: "mbk_not-a-real-key");
        Assert.True(badKey.ExitCode == 1, badKey.Output);
        Assert.Contains("HTTP 401", badKey.Output);
        var badJson = await PublishAsync("job-S13-4", topic.Name, payload: "{not json");
        Assert.True(badJson.ExitCode == 1, badJson.Output);
        Assert.Equal(2, await MessagesAsync());
        Assert.DoesNotContain(publisherKey, first.Output + noTopic.Output + badKey.Output);

        // Unreachable broker: exit 2, so the scheduler can retry the job later.
        var down = await RunScriptAsync(shell, "Publish-BrokerEvent.ps1", publisherKey,
            "-BrokerUrl", $"http://127.0.0.1:{FreePort()}/", "-Topic", topic.Name, "-MessageType", "X.v1",
            "-Payload", "{}", "-IdempotencyKey", "job-S13-5", "-MaxAttempts", "1", "-TimeoutSeconds", "5");
        Assert.True(down.ExitCode == 2, down.Output);

        // The example job: one notification per business date, however often it runs that day.
        string[] job = ["-BrokerUrl", brokerUrl, "-Topic", topic.Name, "-BusinessDate", "2026-10-09"];
        var night = await RunScriptAsync(shell, "Example-NightlyJob.ps1", publisherKey, job);
        Assert.True(night.ExitCode == 0, night.Output);
        var rerun = await RunScriptAsync(shell, "Example-NightlyJob.ps1", publisherKey, job);
        Assert.True(rerun.ExitCode == 0, rerun.Output);
        var notification = Assert.Single(await QueryAsync<(string Type, string Payload)>(
            "SELECT MessageType, Payload FROM broker.Messages WHERE IdempotencyKey = N'nightly-import-2026-10-09'"));
        Assert.Equal("UserNotification", notification.Type);
        Assert.Contains("\"level\":\"Success\"", notification.Payload);
        Assert.Equal(3, await MessagesAsync());
    }
}
