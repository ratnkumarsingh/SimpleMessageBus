using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MessageBroker.IntegrationTests.Infrastructure;

/// <summary>One captured log event with its scopes flattened into a dictionary.</summary>
public sealed record CapturedLog(string Category, LogLevel Level, string Message, string? Exception,
    IReadOnlyDictionary<string, string> Scopes)
{
    /// <summary>Everything that would reach a log sink, as one string for "never logged" checks.</summary>
    public string AllText => $"{Category} {Message} {Exception} {string.Join(" ", Scopes.Select(s => $"{s.Key}={s.Value}"))}";
}

/// <summary>Records every log event, with the scope values a JSON sink would write, for assertions.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<CapturedLog> _logs = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public IReadOnlyList<CapturedLog> Logs => [.. _logs];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scopes = new Dictionary<string, string>();
            provider._scopes.ForEachScope((scope, values) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    foreach (var (key, value) in pairs)
                        values[key] = value?.ToString() ?? "";
                else if (scope is not null)
                    values[$"scope{values.Count}"] = scope.ToString() ?? "";
            }, scopes);
            // Structured state is written by JSON sinks too, so it counts as logged.
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
                foreach (var (key, value) in properties)
                    scopes.TryAdd($"state.{key}", value?.ToString() ?? "");

            provider._logs.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), exception?.ToString(), scopes));
        }
    }
}
