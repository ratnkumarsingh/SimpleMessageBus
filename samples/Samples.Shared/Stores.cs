namespace Samples.Shared;

public sealed record OutboxRow(long OutboxId, string TopicName, string MessageType, string CorrelationId, string Payload, int Attempts);

/// <summary>The publisher's business table and outbox.</summary>
public sealed class OutboxStore(SampleDatabase db)
{
    /// <summary>Records a payment and its event in one transaction; returns the outbox row ID.</summary>
    public Task<long> RecordPaymentAsync(string paymentId, decimal amount, string currency, string topicName, CancellationToken ct = default) =>
        db.QuerySingleAsync<long>("sample.usp_Payment_Record",
            new { PaymentId = paymentId, Amount = amount, Currency = currency, TopicName = topicName }, ct);

    public Task<IReadOnlyList<OutboxRow>> GetPendingAsync(int batchSize, CancellationToken ct = default) =>
        db.QueryAsync<OutboxRow>("sample.usp_Outbox_GetPending", new { BatchSize = batchSize }, ct);

    public Task MarkSentAsync(long outboxId, Guid messageId, CancellationToken ct = default) =>
        db.ExecuteAsync("sample.usp_Outbox_MarkSent", new { OutboxId = outboxId, MessageId = messageId }, ct);

    /// <param name="reject">True when the broker refused the row, so it must not be retried.</param>
    public Task MarkFailedAsync(long outboxId, string error, bool reject, CancellationToken ct = default) =>
        db.ExecuteAsync("sample.usp_Outbox_MarkFailed",
            new { OutboxId = outboxId, Error = error.Length > 1000 ? error[..1000] : error, Reject = reject }, ct);
}

/// <summary>The subscriber's business table, deduplicated through ProcessedMessages.</summary>
public sealed class InvoiceStore(SampleDatabase db)
{
    /// <summary>Marks the invoice paid unless this message was processed before; returns true for a duplicate.</summary>
    public Task<bool> ApplyPaymentAsync(string subscriber, Guid messageId, string paymentId, decimal amount, CancellationToken ct = default) =>
        db.QuerySingleAsync<bool>("sample.usp_Invoice_ApplyPayment",
            new { Subscriber = subscriber, MessageId = messageId, PaymentId = paymentId, Amount = amount }, ct);
}
