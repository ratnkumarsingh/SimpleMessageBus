-- Traceability queries, DLQ listing, heartbeat and retention.

-- Message with its deliveries and every attempt (three result sets). Admins can read any message,
-- publishers only their own. @AppId NULL skips the check (broker-internal callers).
CREATE OR ALTER PROCEDURE broker.usp_Message_GetById
    @MessageId uniqueidentifier,
    @AppId     uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @PublisherAppId uniqueidentifier = (SELECT PublisherAppId FROM broker.Messages WHERE MessageId = @MessageId);
    IF @PublisherAppId IS NULL
        THROW 50404, N'Message was not found.', 1;
    IF @AppId IS NOT NULL AND @AppId <> @PublisherAppId
       AND NOT EXISTS (SELECT 1 FROM broker.Applications WHERE AppId = @AppId AND IsAdmin = 1 AND IsActive = 1)
        THROW 50403, N'Only Admins and the publishing application can read this message.', 1;

    SELECT m.MessageId, t.Name AS TopicName, m.MessageType, m.CorrelationId, m.PublisherAppId, m.IdempotencyKey,
           m.Properties, m.Payload, m.CreatedAt, m.ExpiresAt, ms.Status
    FROM broker.Messages m
    JOIN broker.Topics t ON t.TopicId = m.TopicId
    JOIN broker.vw_MessageStatus ms ON ms.MessageId = m.MessageId
    WHERE m.MessageId = @MessageId;

    SELECT d.DeliveryId, d.SubscriptionId, s.Name AS SubscriptionName, s.DeliveryMode, d.Status,
           d.AttemptCount, d.TotalAttemptCount, d.AvailableAt, d.LockedUntil, d.ExpiresAt, d.CompletedAt,
           dl.Reason AS DeadLetterReason
    FROM broker.Deliveries d
    JOIN broker.Subscriptions s ON s.SubscriptionId = d.SubscriptionId
    LEFT JOIN broker.DeadLetters dl ON dl.DeliveryId = d.DeliveryId AND dl.RequeuedAt IS NULL
    WHERE d.MessageId = @MessageId
    ORDER BY d.DeliveryId;

    SELECT a.AttemptId, a.DeliveryId, a.AttemptNumber, a.Channel, a.LeasedAt, a.EndedAt, a.Outcome,
           a.HttpStatusCode, a.DurationMs, a.ErrorCode, a.ErrorMessage, a.ErrorDetail
    FROM broker.DeliveryAttempts a
    JOIN broker.Deliveries d ON d.DeliveryId = a.DeliveryId
    WHERE d.MessageId = @MessageId
    ORDER BY a.DeliveryId, a.AttemptNumber;
END
GO

CREATE OR ALTER PROCEDURE broker.usp_Message_ListByCorrelation
    @CorrelationId nvarchar(100),
    @MaxRows       int = 500
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@MaxRows)
           m.MessageId, t.Name AS TopicName, m.MessageType, m.CorrelationId, m.PublisherAppId,
           m.CreatedAt, m.ExpiresAt, ms.Status, ms.DeliveryCount
    FROM broker.Messages m
    JOIN broker.Topics t ON t.TopicId = m.TopicId
    JOIN broker.vw_MessageStatus ms ON ms.MessageId = m.MessageId
    WHERE m.CorrelationId = @CorrelationId
    ORDER BY m.MessageSeq;
END
GO

-- Keyset paging, newest first: pass the smallest DeadLetterId of the previous page as @BeforeId.
-- Admins and applications with Receive on the subscription (its owner) may list.
CREATE OR ALTER PROCEDURE broker.usp_DeadLetter_List
    @SubscriptionId  uniqueidentifier,
    @AppId           uniqueidentifier = NULL,
    @PageSize        int = 50,
    @BeforeId        bigint = NULL,
    @IncludeRequeued bit = 0
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM broker.Subscriptions WHERE SubscriptionId = @SubscriptionId)
        THROW 50404, N'Subscription was not found.', 1;
    IF @AppId IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM broker.fn_HasPermission(@AppId, 'Subscription', @SubscriptionId, 'Receive') WHERE Allowed = 1)
        THROW 50403, N'The application is not permitted to view this subscription''s dead letters.', 1;

    SELECT TOP (@PageSize) *
    FROM broker.vw_DeadLetterDetails
    WHERE SubscriptionId = @SubscriptionId
      AND (@IncludeRequeued = 1 OR RequeuedAt IS NULL)
      AND (@BeforeId IS NULL OR DeadLetterId < @BeforeId)
    ORDER BY DeadLetterId DESC;
END
GO

CREATE OR ALTER PROCEDURE broker.usp_Heartbeat_Write
    @InstanceId nvarchar(200)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    UPDATE broker.BrokerHeartbeats SET LastBeatAt = SYSUTCDATETIME() WHERE InstanceId = @InstanceId;
    IF @@ROWCOUNT = 0
        INSERT broker.BrokerHeartbeats (InstanceId, LastBeatAt) VALUES (@InstanceId, SYSUTCDATETIME());
END
GO

-- Returns the newest heartbeat and the database clock, so the age is measured on one clock.
CREATE OR ALTER PROCEDURE broker.usp_Heartbeat_GetLatest
AS
BEGIN
    SET NOCOUNT ON;
    -- DbNow at LastBeatAt's precision: datetime2(3) rounds, and a beat written a moment ago could
    -- otherwise read as slightly in the future.
    SELECT MAX(LastBeatAt) AS LastBeatAt, CAST(SYSUTCDATETIME() AS datetime2(3)) AS DbNow FROM broker.BrokerHeartbeats;
END
GO

-- Hourly purge (spec section 7, retention). Deletes in batches to avoid lock escalation:
--   * Completed and Cancelled deliveries, with attempts, @CompletedDays after completion
--   * Dead-lettered deliveries @DeadLetterDays after dead-lettering
--   * Messages with no remaining deliveries, @CompletedDays after creation
--     (covers messages that never had subscriptions, and keeps any message still in the DLQ)
CREATE OR ALTER PROCEDURE broker.usp_Retention_Purge
    @CompletedDays  int = 14,
    @DeadLetterDays int = 90,
    @BatchSize      int = 5000
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    DECLARE @Now datetime2(3) = SYSUTCDATETIME();
    DECLARE @CompletedCutoff datetime2(3) = DATEADD(day, -@CompletedDays, @Now);
    DECLARE @DeadLetterCutoff datetime2(3) = DATEADD(day, -@DeadLetterDays, @Now);
    -- Each delivery has a handful of attempts, so fewer deliveries per batch keeps every DELETE small.
    DECLARE @DeliveryBatch int = CASE WHEN @BatchSize / 5 < 1 THEN 1 ELSE @BatchSize / 5 END;
    DECLARE @DeliveriesDeleted int = 0, @MessagesDeleted int = 0, @Rows int;
    DECLARE @Batch TABLE (DeliveryId bigint PRIMARY KEY);

    WHILE 1 = 1
    BEGIN
        DELETE @Batch;

        INSERT @Batch (DeliveryId)
        SELECT TOP (@DeliveryBatch) d.DeliveryId
        FROM broker.Deliveries d
        WHERE (d.Status IN (2, 4) AND d.CompletedAt < @CompletedCutoff)
           OR (d.Status = 3 AND EXISTS (SELECT 1 FROM broker.DeadLetters dl
                                        WHERE dl.DeliveryId = d.DeliveryId AND dl.RequeuedAt IS NULL
                                          AND dl.DeadLetteredAt < @DeadLetterCutoff));

        IF @@ROWCOUNT = 0 BREAK;

        BEGIN TRAN;
        DELETE a FROM broker.DeliveryAttempts a JOIN @Batch b ON b.DeliveryId = a.DeliveryId;
        DELETE dl FROM broker.DeadLetters dl JOIN @Batch b ON b.DeliveryId = dl.DeliveryId;
        DELETE d FROM broker.Deliveries d JOIN @Batch b ON b.DeliveryId = d.DeliveryId;
        SET @DeliveriesDeleted += @@ROWCOUNT;
        COMMIT;
    END

    WHILE 1 = 1
    BEGIN
        DELETE TOP (@BatchSize) m
        FROM broker.Messages m
        WHERE m.CreatedAt < @CompletedCutoff
          AND NOT EXISTS (SELECT 1 FROM broker.Deliveries d WHERE d.MessageId = m.MessageId);

        SET @Rows = @@ROWCOUNT;
        SET @MessagesDeleted += @Rows;
        IF @Rows < @BatchSize BREAK;
    END

    DELETE broker.BrokerHeartbeats WHERE LastBeatAt < DATEADD(day, -1, @Now);

    SELECT @DeliveriesDeleted AS DeliveriesDeleted, @MessagesDeleted AS MessagesDeleted;
END
GO

-- Readiness probe: proves the connection works and the broker schema is deployed.
CREATE OR ALTER PROCEDURE broker.usp_Health_Ping
AS
BEGIN
    SET NOCOUNT ON;
    SELECT 1 AS Ok;
END
GO
