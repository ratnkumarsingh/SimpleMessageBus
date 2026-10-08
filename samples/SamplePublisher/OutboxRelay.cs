using System.Net;
using System.Text.Json;
using MessageBroker.Contracts.Client;
using MessageBroker.Contracts.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Samples.Shared;

namespace SamplePublisher;

/// <param name="Sent">Rows the broker accepted in this pass, duplicates included.</param>
/// <param name="Faulted">The broker could not be reached or was unavailable; the pass stopped early.</param>
public sealed record RelayPass(int Sent, int Rejected, bool Faulted);

/// <summary>
/// Posts outbox rows to the broker in order (spec section 10, "Publisher outage handling"). Each row
/// is published with the Idempotency-Key <c>outbox-&lt;OutboxId&gt;</c>, so a retry after a lost response
/// or an outage returns the original message instead of creating a second one. When the broker is
/// unreachable or answers 5xx, 408 or 429, the pass stops and the relay backs off; rows stay pending.
/// A 400 or 413 means the row itself is bad: it is marked rejected and the relay moves on.
/// </summary>
public sealed class OutboxRelay(OutboxStore outbox, BrokerClient broker, ILogger<OutboxRelay> logger, TimeProvider time) : BackgroundService
{
    public const int BatchSize = 50;
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    public static string IdempotencyKey(long outboxId) => $"outbox-{outboxId:D9}";

    public async Task<RelayPass> RunOnceAsync(CancellationToken ct)
    {
        int sent = 0, rejected = 0;
        foreach (var row in await outbox.GetPendingAsync(BatchSize, ct))
        {
            try
            {
                using var payload = JsonDocument.Parse(row.Payload);
                var result = await broker.PublishAsync(row.TopicName, new PublishRequest
                {
                    MessageType = row.MessageType,
                    CorrelationId = row.CorrelationId,
                    Payload = payload.RootElement,
                }, IdempotencyKey(row.OutboxId), ct);

                await outbox.MarkSentAsync(row.OutboxId, result.MessageId, ct);
                sent++;
                logger.LogInformation("Outbox row {OutboxId} published as message {MessageId}{Duplicate}",
                    row.OutboxId, result.MessageId, result.IsDuplicate ? " (already accepted earlier)" : "");
            }
            catch (BrokerApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge)
            {
                await outbox.MarkFailedAsync(row.OutboxId, $"{(int)ex.StatusCode} {ex.Message}", reject: true, ct);
                rejected++;
                logger.LogError("Outbox row {OutboxId} rejected by the broker: {Status} {Error}", row.OutboxId, (int)ex.StatusCode, ex.Message);
            }
            catch (Exception ex) when (IsRetryable(ex, ct))
            {
                // Keep order: stop here and try this row again after the backoff, with the same key.
                await outbox.MarkFailedAsync(row.OutboxId, Describe(ex), reject: false, ct);
                logger.LogWarning("Broker unavailable for outbox row {OutboxId} (attempt {Attempt}): {Error}",
                    row.OutboxId, row.Attempts + 1, Describe(ex));
                return new RelayPass(sent, rejected, Faulted: true);
            }
        }
        return new RelayPass(sent, rejected, Faulted: false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.Zero;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pass = await RunOnceAsync(stoppingToken);
                backoff = pass.Faulted ? NextBackoff(backoff) : TimeSpan.Zero;
                if (pass.Faulted)
                    logger.LogInformation("Retrying the outbox in {Seconds} s", backoff.TotalSeconds);
                else if (pass.Sent + pass.Rejected == BatchSize)
                    continue; // more rows are waiting
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                backoff = NextBackoff(backoff);
                logger.LogError(ex, "Outbox pass failed; retrying in {Seconds} s", backoff.TotalSeconds);
            }

            try
            {
                await Task.Delay(backoff > TimeSpan.Zero ? backoff : PollInterval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static TimeSpan NextBackoff(TimeSpan current) =>
        current == TimeSpan.Zero ? TimeSpan.FromSeconds(1) : TimeSpan.FromTicks(Math.Min(current.Ticks * 2, MaxBackoff.Ticks));

    /// <summary>Unreachable, timed out, or a status that may succeed later. 401/403/404 also retry: they are configuration problems an operator can fix.</summary>
    private static bool IsRetryable(Exception ex, CancellationToken ct) => ex switch
    {
        BrokerApiException => true, // 400 and 413 are handled above
        HttpRequestException => true,
        TaskCanceledException => !ct.IsCancellationRequested, // HttpClient timeout
        _ => false,
    };

    private static string Describe(Exception ex) => ex switch
    {
        BrokerApiException api => $"{(int)api.StatusCode} {api.Message}",
        TaskCanceledException => "Timed out",
        _ => ex.Message,
    };
}
