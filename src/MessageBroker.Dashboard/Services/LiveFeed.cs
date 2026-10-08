using System.Security.Claims;
using MessageBroker.Contracts;
using MessageBroker.Contracts.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace MessageBroker.Dashboard.Services;

public enum LiveState
{
    Connecting,
    Live,
    Reconnecting,
    /// <summary>The hub is unreachable; pages fall back to polling.</summary>
    Offline,
}

/// <summary>Live broker activity for one circuit. Pages subscribe to the events and re-read what they show.</summary>
public interface ILiveFeed
{
    LiveState State { get; }

    /// <summary>Messages published and deliveries changed, in batches.</summary>
    event Action<IReadOnlyList<MessageActivity>>? Activity;

    /// <summary>Counts changed; also raised every FallbackPollSeconds while the feed is not <see cref="LiveState.Live"/>.</summary>
    event Action? OverviewChanged;

    event Action? StateChanged;

    /// <summary>Connects on first call; later calls do nothing.</summary>
    Task StartAsync();
}

/// <summary>
/// A connection to the broker's admin hub with the operator's key. It reconnects forever; while it is
/// not live, a timer raises <see cref="OverviewChanged"/> so pages keep refreshing by polling.
/// </summary>
public sealed class BrokerLiveFeed(
    IOptions<DashboardOptions> options,
    AuthenticationStateProvider auth,
    TimeProvider time,
    ILogger<BrokerLiveFeed> logger) : ILiveFeed, IAsyncDisposable
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private readonly CancellationTokenSource _stop = new();
    private HubConnection? _connection;
    private Task? _running;

    public LiveState State { get; private set; } = LiveState.Connecting;

    public event Action<IReadOnlyList<MessageActivity>>? Activity;
    public event Action? OverviewChanged;
    public event Action? StateChanged;

    public async Task StartAsync()
    {
        if (_running is not null)
            return;
        var key = (await auth.GetAuthenticationStateAsync()).User.FindFirstValue(DashboardClaims.ApiKey);
        if (key is null)
            return;

        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(options.Value.BrokerUrl, AdminHub.Path), o => o.AccessTokenProvider = () => Task.FromResult<string?>(key))
            .WithAutomaticReconnect(new ForeverRetry())
            .Build();
        _connection.On<MessageActivity[]>(AdminHub.Activity, batch => Activity?.Invoke(batch));
        _connection.On(AdminHub.OverviewChanged, () => OverviewChanged?.Invoke());
        _connection.Reconnecting += _ =>
        {
            SetState(LiveState.Reconnecting);
            return Task.CompletedTask;
        };
        _connection.Reconnected += _ =>
        {
            SetState(LiveState.Live);
            OverviewChanged?.Invoke(); // events were missed while away
            return Task.CompletedTask;
        };
        _connection.Closed += _ =>
        {
            SetState(LiveState.Offline);
            return Task.CompletedTask;
        };

        _running = RunAsync(_connection, _stop.Token);
    }

    /// <summary>Connects (retrying while the broker is down) and polls while offline.</summary>
    private async Task RunAsync(HubConnection connection, CancellationToken ct)
    {
        var poll = TimeSpan.FromSeconds(options.Value.FallbackPollSeconds);
        while (!ct.IsCancellationRequested)
        {
            if (connection.State == HubConnectionState.Disconnected)
            {
                try
                {
                    await connection.StartAsync(ct);
                    SetState(LiveState.Live);
                    OverviewChanged?.Invoke();
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogInformation("Admin hub unavailable ({Error}); polling every {Seconds} s", ex.Message, poll.TotalSeconds);
                    SetState(LiveState.Offline);
                }
            }

            try
            {
                await Task.Delay(poll, time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            // Reconnecting can last indefinitely (the retry never gives up), so poll then too.
            if (State != LiveState.Live)
                OverviewChanged?.Invoke();
        }
    }

    private void SetState(LiveState state)
    {
        if (State == state)
            return;
        State = state;
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_connection is not null)
            await _connection.DisposeAsync();
        if (_running is not null)
            await _running.ContinueWith(_ => { }, TaskScheduler.Default);
        _stop.Dispose();
    }

    private sealed class ForeverRetry : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context) =>
            context.PreviousRetryCount < RetryDelays.Length ? RetryDelays[context.PreviousRetryCount] : TimeSpan.FromSeconds(30);
    }
}

/// <summary>
/// Runs a refresh at most once per interval: the first request schedules it, later requests until it
/// runs are absorbed. Unlike a debounce it still fires under a steady stream of events.
/// </summary>
public sealed class RefreshThrottle(TimeSpan interval, TimeProvider time) : IDisposable
{
    private readonly CancellationTokenSource _disposed = new();
    private int _scheduled;

    public void Request(Func<Task> refresh)
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1)
            return;
        _ = RunAsync(refresh);
    }

    private async Task RunAsync(Func<Task> refresh)
    {
        try
        {
            if (interval > TimeSpan.Zero)
                await Task.Delay(interval, time, _disposed.Token);
            Volatile.Write(ref _scheduled, 0);
            await refresh();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _disposed.Cancel();
        _disposed.Dispose();
    }
}
