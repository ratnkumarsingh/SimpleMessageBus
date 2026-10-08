-- [Fix 12] Derived message status. Cancelled deliveries are ignored: a deleted subscription says
-- nothing about whether the message was processed. A message with no deliveries is Completed.
CREATE OR ALTER VIEW broker.vw_MessageStatus
AS
SELECT
    m.MessageId,
    CASE
        WHEN SUM(CASE WHEN d.Status IN (0, 1) THEN 1 ELSE 0 END) > 0 THEN 'InProgress'
        WHEN SUM(CASE WHEN d.Status = 3 THEN 1 ELSE 0 END) = 0 THEN 'Completed'
        WHEN SUM(CASE WHEN d.Status = 3 THEN 1 ELSE 0 END) = SUM(CASE WHEN d.Status <> 4 THEN 1 ELSE 0 END) THEN 'DeadLettered'
        ELSE 'PartiallyDeadLettered'
    END AS Status,
    COUNT(d.DeliveryId) AS DeliveryCount,
    SUM(CASE WHEN d.Status = 0 THEN 1 ELSE 0 END) AS PendingCount,
    SUM(CASE WHEN d.Status = 1 THEN 1 ELSE 0 END) AS LeasedCount,
    SUM(CASE WHEN d.Status = 2 THEN 1 ELSE 0 END) AS CompletedCount,
    SUM(CASE WHEN d.Status = 3 THEN 1 ELSE 0 END) AS DeadLetteredCount,
    SUM(CASE WHEN d.Status = 4 THEN 1 ELSE 0 END) AS CancelledCount
FROM broker.Messages m
LEFT JOIN broker.Deliveries d ON d.MessageId = m.MessageId
GROUP BY m.MessageId;
GO

-- Backlog and DLQ size per subscription (spec section 13, operational queries).
CREATE OR ALTER VIEW broker.vw_SubscriptionDeliveryCounts
AS
SELECT
    s.SubscriptionId,
    SUM(CASE WHEN d.Status = 0 THEN 1 ELSE 0 END) AS PendingCount,
    SUM(CASE WHEN d.Status = 1 THEN 1 ELSE 0 END) AS LeasedCount,
    SUM(CASE WHEN d.Status = 3 THEN 1 ELSE 0 END) AS DeadLetteredCount
FROM broker.Subscriptions s
LEFT JOIN broker.Deliveries d ON d.SubscriptionId = s.SubscriptionId AND d.Status IN (0, 1, 3)
GROUP BY s.SubscriptionId;
GO

-- Subscription with its topic name and delivery counts; the shape returned by the subscription procedures.
CREATE OR ALTER VIEW broker.vw_SubscriptionDetails
AS
SELECT
    s.SubscriptionId,
    s.TopicId,
    t.Name AS TopicName,
    s.Name,
    s.OwnerAppId,
    s.Status,
    s.DeliveryMode,
    s.WebhookUrl,
    s.WebhookSecret,
    s.PreviousWebhookSecret,
    s.PreviousSecretExpiresAt,
    s.WebhookTimeoutSeconds,
    s.MaxConcurrentDeliveries,
    s.MaxAttempts,
    s.LockDurationSeconds,
    s.RetryBaseDelaySeconds,
    s.RetryMaxDelaySeconds,
    s.TtlSeconds,
    s.CreatedAt,
    s.UpdatedAt,
    c.PendingCount,
    c.LeasedCount,
    c.DeadLetteredCount
FROM broker.Subscriptions s
JOIN broker.Topics t ON t.TopicId = s.TopicId
JOIN broker.vw_SubscriptionDeliveryCounts c ON c.SubscriptionId = s.SubscriptionId;
GO

-- A DLQ entry with the message and names an operator needs, without copying the payload.
CREATE OR ALTER VIEW broker.vw_DeadLetterDetails
AS
SELECT
    dl.DeadLetterId,
    dl.DeliveryId,
    dl.MessageId,
    dl.SubscriptionId,
    s.Name AS SubscriptionName,
    dl.TopicId,
    t.Name AS TopicName,
    dl.Reason,
    dl.AttemptCount,
    dl.LastError,
    dl.FirstFailureAt,
    dl.LastFailureAt,
    dl.DeadLetteredAt,
    dl.RequeuedAt,
    dl.RequeuedBy,
    m.MessageType,
    m.CorrelationId,
    m.CreatedAt AS MessageCreatedAt,
    m.Properties,
    m.Payload
FROM broker.DeadLetters dl
JOIN broker.Messages m ON m.MessageId = dl.MessageId
JOIN broker.Subscriptions s ON s.SubscriptionId = dl.SubscriptionId
JOIN broker.Topics t ON t.TopicId = dl.TopicId;
GO

-- Push subscriptions the lease loop works on. HasDueDeliveries lets it skip idle subscriptions cheaply.
CREATE OR ALTER VIEW broker.vw_ActivePushSubscriptions
AS
SELECT
    s.SubscriptionId,
    s.TopicId,
    s.Name,
    s.DeliveryMode,
    s.WebhookUrl,
    s.WebhookSecret,
    s.PreviousWebhookSecret,
    s.PreviousSecretExpiresAt,
    s.WebhookTimeoutSeconds,
    s.MaxConcurrentDeliveries,
    s.LockDurationSeconds,
    -- Compared with the clock at millisecond precision, as usp_Delivery_Lease does: a stored
    -- datetime2(3) can be rounded up, and against the raw clock a delivery published within the last
    -- half millisecond would look not yet due, so the lease loop would skip it until its next pass.
    CAST(CASE WHEN EXISTS (
        SELECT 1 FROM broker.Deliveries d
        WHERE d.SubscriptionId = s.SubscriptionId
          AND d.Status = 0
          AND d.AvailableAt <= CAST(SYSUTCDATETIME() AS datetime2(3))
          AND (d.ExpiresAt IS NULL OR d.ExpiresAt > CAST(SYSUTCDATETIME() AS datetime2(3)))) THEN 1 ELSE 0 END AS bit) AS HasDueDeliveries
FROM broker.Subscriptions s
WHERE s.Status = 'Active'
  AND s.DeliveryMode IN ('Webhook', 'SignalR');
GO
