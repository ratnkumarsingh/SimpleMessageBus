using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MessageBroker.IntegrationTests.Infrastructure;

public sealed record RecordedCall(string Path, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string Header(string name) => Headers[name];
}

/// <summary>
/// A real HTTP endpoint on a free local port that stands in for a subscriber's webhook. It records
/// every request and answers with the configured status, body, headers and delay; unknown paths get 404.
/// </summary>
public sealed class FakeWebhookEndpoint : IAsyncDisposable
{
    private sealed record Reply(int Status, string? Body, TimeSpan Delay, IReadOnlyDictionary<string, string> Headers);

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<RecordedCall> _calls = new();
    private readonly ConcurrentDictionary<string, Reply> _replies = new(StringComparer.OrdinalIgnoreCase);

    private FakeWebhookEndpoint(WebApplication app) => _app = app;

    public string Url { get; private set; } = "";

    public static async Task<FakeWebhookEndpoint> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var endpoint = new FakeWebhookEndpoint(builder.Build());
        endpoint._app.Run(endpoint.HandleAsync);
        await endpoint._app.StartAsync();
        endpoint.Url = endpoint._app.Urls.Single();
        return endpoint;
    }

    public void Respond(string path, int status, string? body = null, TimeSpan? delay = null, IReadOnlyDictionary<string, string>? headers = null) =>
        _replies[path] = new Reply(status, body, delay ?? TimeSpan.Zero, headers ?? new Dictionary<string, string>());

    public void ResetReplies() => _replies.Clear();

    public IReadOnlyList<RecordedCall> Calls(string path) => _calls.Where(c => c.Path == path).ToList();

    private async Task HandleAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        _calls.Enqueue(new RecordedCall(
            context.Request.Path,
            context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            body));

        if (!_replies.TryGetValue(context.Request.Path, out var reply))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (reply.Delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(reply.Delay, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                return; // the caller gave up
            }
        }

        context.Response.StatusCode = reply.Status;
        foreach (var (name, value) in reply.Headers)
            context.Response.Headers[name] = value;
        if (reply.Body is not null)
            await context.Response.WriteAsync(reply.Body, context.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
