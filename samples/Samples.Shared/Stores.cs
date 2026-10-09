using Microsoft.Data.SqlClient;
using static Samples.Shared.SampleDatabase;

namespace Samples.Shared;

public sealed record OutboxRow(long OutboxId, string TopicName, string MessageType, string CorrelationId, string Payload, int Attempts)
{
    internal static OutboxRow Read(SqlDataReader r) => new(
        r.GetInt64(r.GetOrdinal("OutboxId")),
        r.GetString(r.GetOrdinal("TopicName")),
        r.GetString(r.GetOrdinal("MessageType")),
        r.GetString(r.GetOrdinal("CorrelationId")),
        r.GetString(r.GetOrdinal("Payload")),
        r.GetInt32(r.GetOrdinal("Attempts")));
}

/// <summary>The publisher's business table and outbox.</summary>
public sealed class OutboxStore(SampleDatabase db)
{
    /// <summary>Records a payment and its event in one transaction; returns the outbox row ID.</summary>
    public Task<long> RecordPaymentAsync(string paymentId, decimal amount, string currency, string topicName, CancellationToken ct = default) =>
        db.QuerySingleAsync("sample.usp_Payment_Record", r => Convert.ToInt64(r.GetValue(0)), ct,
            Param("PaymentId", paymentId), Param("Amount", amount), Param("Currency", currency), Param("TopicName", topicName));

    public Task<IReadOnlyList<OutboxRow>> GetPendingAsync(int batchSize, CancellationToken ct = default) =>
        db.QueryAsync("sample.usp_Outbox_GetPending", OutboxRow.Read, ct, Param("BatchSize", batchSize));

    public Task MarkSentAsync(long outboxId, Guid messageId, CancellationToken ct = default) =>
        db.ExecuteAsync("sample.usp_Outbox_MarkSent", ct, Param("OutboxId", outboxId), Param("MessageId", messageId));

    /// <param name="reject">True when the broker refused the row, so it must not be retried.</param>
    public Task MarkFailedAsync(long outboxId, string error, bool reject, CancellationToken ct = default) =>
        db.ExecuteAsync("sample.usp_Outbox_MarkFailed", ct,
            Param("OutboxId", outboxId), Param("Error", error.Length > 1000 ? error[..1000] : error), Param("Reject", reject));
}

/// <summary>The subscriber's business table, deduplicated through ProcessedMessages.</summary>
public sealed class InvoiceStore(SampleDatabase db)
{
    /// <summary>Marks the invoice paid unless this message was processed before; returns true for a duplicate.</summary>
    public Task<bool> ApplyPaymentAsync(string subscriber, Guid messageId, string paymentId, decimal amount, CancellationToken ct = default) =>
        db.QuerySingleAsync("sample.usp_Invoice_ApplyPayment", r => Convert.ToBoolean(r.GetValue(0)), ct,
            Param("Subscriber", subscriber), Param("MessageId", messageId), Param("PaymentId", paymentId), Param("Amount", amount));
}
