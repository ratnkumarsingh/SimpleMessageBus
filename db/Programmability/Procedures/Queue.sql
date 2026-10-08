-- Lease, settle, expiry and requeue (spec sections 7, 8 and 10).
-- Every time comes from SYSUTCDATETIME() so the database clock alone decides leases and retries [Fix 4].

-- Throws 50404 when the delivery does not exist, 50403 when @AppId may not settle it.
-- @AppId NULL means the broker itself (dispatcher, maintenance).
CREATE OR ALTER PROCEDURE broker.usp_Delivery_CheckAccess
    @DeliveryId bigint,
    @AppId      uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @SubscriptionId uniqueidentifier = (SELECT SubscriptionId FROM broker.Deliveries WHERE DeliveryId = @DeliveryId);
    IF @SubscriptionId IS NULL
        THROW 50404, N'Delivery was not found.', 1;

    IF @AppId IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM broker.fn_HasPermission(@AppId, 'Subscription', @SubscriptionId, 'Receive') WHERE Allowed = 1)
        THROW 50403, N'The application is not permitted to settle deliveries of this subscription.', 1;
END
GO

-- Shared failure path for NACK, failed webhook calls and lease expiry. The caller must hold an
-- update lock on the Leased delivery inside its own transaction.
-- Every read or write of one delivery's DeliveryAttempts and DeadLetters rows seeks
-- UQ_DeliveryAttempts_Number or IX_DeadLetters_Delivery: FORCESEEK on reads, an INDEX hint in
-- the FROM clause of updates (FORCESEEK is not allowed on update targets). On a small table the optimizer would
-- otherwise scan the clustered index, and two settles of different deliveries deadlock on each
-- other's attempt rows (seen with concurrent webhook NACKs).
CREATE OR ALTER PROCEDURE broker.usp_Delivery_RecordFailure
    @DeliveryId      bigint,
    @Now             datetime2(3),
    @Outcome         varchar(20),
    @ErrorCode       nvarchar(100),
    @ErrorMessage    nvarchar(1000),
    @ErrorDetail     nvarchar(max),
    @HttpStatusCode  int,
    @DeadLetter      bit,
    @DeadLettered    bit OUTPUT,
    @NextAvailableAt datetime2(3) OUTPUT
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    DECLARE @AttemptCount int, @MaxAttempts int, @BaseDelay int, @MaxDelay int,
            @MessageId uniqueidentifier, @SubscriptionId uniqueidentifier, @TopicId uniqueidentifier;

    SELECT @AttemptCount = d.AttemptCount, @MaxAttempts = s.MaxAttempts,
           @BaseDelay = s.RetryBaseDelaySeconds, @MaxDelay = s.RetryMaxDelaySeconds,
           @MessageId = d.MessageId, @SubscriptionId = d.SubscriptionId, @TopicId = s.TopicId
    FROM broker.Deliveries d
    JOIN broker.Subscriptions s ON s.SubscriptionId = d.SubscriptionId
    WHERE d.DeliveryId = @DeliveryId;

    UPDATE a
    SET EndedAt = @Now,
        Outcome = @Outcome,
        HttpStatusCode = @HttpStatusCode,
        DurationMs = DATEDIFF(millisecond, LeasedAt, @Now),
        ErrorCode = @ErrorCode,
        ErrorMessage = @ErrorMessage,
        ErrorDetail = @ErrorDetail
    FROM broker.DeliveryAttempts a WITH (INDEX (UQ_DeliveryAttempts_Number))
    WHERE a.DeliveryId = @DeliveryId AND a.EndedAt IS NULL;

    IF @DeadLetter = 1 OR @AttemptCount >= @MaxAttempts
    BEGIN
        UPDATE broker.Deliveries SET Status = 3, LockToken = NULL, LockedUntil = NULL WHERE DeliveryId = @DeliveryId;

        -- First failure of the current cycle: attempts since the last requeue, if any.
        DECLARE @LastRequeue datetime2(3) = (SELECT MAX(RequeuedAt) FROM broker.DeadLetters WITH (FORCESEEK) WHERE DeliveryId = @DeliveryId);

        INSERT broker.DeadLetters
            (DeliveryId, MessageId, SubscriptionId, TopicId, Reason, AttemptCount, LastError, FirstFailureAt, LastFailureAt, DeadLetteredAt)
        SELECT @DeliveryId, @MessageId, @SubscriptionId, @TopicId,
               CASE WHEN @DeadLetter = 1 THEN 'RejectedBySubscriber' ELSE 'MaxAttemptsExceeded' END,
               @AttemptCount,
               COALESCE(@ErrorMessage, @ErrorCode),
               (SELECT MIN(EndedAt) FROM broker.DeliveryAttempts WITH (FORCESEEK)
                WHERE DeliveryId = @DeliveryId
                  AND Outcome IN ('Nacked', 'LeaseExpired')
                  AND (@LastRequeue IS NULL OR LeasedAt >= @LastRequeue)),
               @Now,
               @Now;

        SET @DeadLettered = 1;
        SET @NextAvailableAt = NULL;
    END
    ELSE
    BEGIN
        -- delay(n) = min(base * 2^(n-1), max) * (1 +/- 0.2 jitter), spec section 10.
        DECLARE @DelaySeconds float = @BaseDelay * POWER(CAST(2 AS float), @AttemptCount - 1);
        IF @DelaySeconds > @MaxDelay SET @DelaySeconds = @MaxDelay;
        DECLARE @DelayMs int = CAST(@DelaySeconds * 1000.0 * (0.8 + 0.4 * RAND(CHECKSUM(NEWID()))) AS int);

        SET @NextAvailableAt = DATEADD(millisecond, @DelayMs, @Now);
        SET @DeadLettered = 0;

        UPDATE broker.Deliveries
        SET Status = 0, LockToken = NULL, LockedUntil = NULL, AvailableAt = @NextAvailableAt
        WHERE DeliveryId = @DeliveryId;
    END
END
GO

-- [Fix 3] The spec's atomic lease statement plus the attempt rows, in one transaction.
-- [Fix 12] Paused subscriptions lease nothing, whether pushed or pulled.
CREATE OR ALTER PROCEDURE broker.usp_Delivery_Lease
    @SubscriptionId uniqueidentifier,
    @MaxMessages    int,
    @Channel        varchar(10),
    @AppId          uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    DECLARE @Status varchar(10), @Mode varchar(10), @LockSeconds int;
    SELECT @Status = Status, @Mode = DeliveryMode, @LockSeconds = LockDurationSeconds
    FROM broker.Subscriptions WHERE SubscriptionId = @SubscriptionId;

    IF @Status IS NULL OR @Status = 'Deleted'
        THROW 50404, N'Subscription was not found.', 1;
    IF @AppId IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM broker.fn_HasPermission(@AppId, 'Subscription', @SubscriptionId, 'Receive') WHERE Allowed = 1)
        THROW 50403, N'The application is not permitted to receive from this subscription.', 1;
    IF @Channel = 'Pull' AND @Mode <> 'Pull'
        THROW 50409, N'The subscription does not use Pull delivery.', 1;
    IF @MaxMessages < 1 OR @MaxMessages > 256
        THROW 50400, N'maxMessages is out of range.', 1;

    DECLARE @Leased TABLE
    (
        DeliveryId        bigint PRIMARY KEY,
        MessageId         uniqueidentifier,
        LockToken         uniqueidentifier,
        LockedUntil       datetime2(3),
        AttemptCount      int,
        TotalAttemptCount int
    );
    DECLARE @Now datetime2(3) = SYSUTCDATETIME();

    IF @Status = 'Active'
    BEGIN
        BEGIN TRAN;

        WITH next AS
        (
            SELECT TOP (@MaxMessages) DeliveryId, MessageId, Status, LockToken, LockedUntil, AttemptCount, TotalAttemptCount
            FROM broker.Deliveries WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE SubscriptionId = @SubscriptionId
              AND Status = 0
              AND AvailableAt <= @Now
              AND (ExpiresAt IS NULL OR ExpiresAt > @Now)
            ORDER BY AvailableAt, DeliveryId
        )
        UPDATE next
        SET Status = 1,
            LockToken = NEWID(),
            LockedUntil = DATEADD(second, @LockSeconds, @Now),
            AttemptCount = AttemptCount + 1,
            TotalAttemptCount = TotalAttemptCount + 1
        OUTPUT inserted.DeliveryId, inserted.MessageId, inserted.LockToken, inserted.LockedUntil,
               inserted.AttemptCount, inserted.TotalAttemptCount
        INTO @Leased;

        INSERT broker.DeliveryAttempts (DeliveryId, AttemptNumber, Channel, LeasedAt)
        SELECT DeliveryId, TotalAttemptCount, @Channel, @Now FROM @Leased;

        COMMIT;
    END

    SELECT l.DeliveryId, l.LockToken, l.LockedUntil, l.AttemptCount AS Attempt,
           m.MessageId, m.MessageType, m.CorrelationId, m.CreatedAt, m.Properties, m.Payload
    FROM @Leased l
    JOIN broker.Messages m ON m.MessageId = l.MessageId
    ORDER BY l.DeliveryId;
END
GO

-- Spec settle statement. 0 rows means the lease was lost: 50410, which the API returns as 410 Gone.
CREATE OR ALTER PROCEDURE broker.usp_Delivery_Ack
    @DeliveryId     bigint,
    @LockToken      uniqueidentifier,
    @AppId          uniqueidentifier = NULL,
    @HttpStatusCode int = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    EXEC broker.usp_Delivery_CheckAccess @DeliveryId, @AppId;

    DECLARE @Now datetime2(3) = SYSUTCDATETIME();

    BEGIN TRAN;

    UPDATE broker.Deliveries
    SET Status = 2, CompletedAt = @Now, LockToken = NULL, LockedUntil = NULL
    WHERE DeliveryId = @DeliveryId
      AND LockToken = @LockToken
      AND Status = 1
      AND LockedUntil > @Now;

    IF @@ROWCOUNT = 0
    BEGIN
        ROLLBACK;
        THROW 50410, N'The lease was lost: the lock token is stale or the lease has expired.', 1;
    END

    UPDATE a
    SET EndedAt = @Now, Outcome = 'Acked', HttpStatusCode = @HttpStatusCode, DurationMs = DATEDIFF(millisecond, LeasedAt, @Now)
    FROM broker.DeliveryAttempts a WITH (INDEX (UQ_DeliveryAttempts_Number))
    WHERE a.DeliveryId = @DeliveryId AND a.EndedAt IS NULL;

    COMMIT;

    -- For the admin activity feed.
    SELECT MessageId FROM broker.Deliveries WHERE DeliveryId = @DeliveryId;
END
GO

CREATE OR ALTER PROCEDURE broker.usp_Delivery_Nack
    @DeliveryId     bigint,
    @LockToken      uniqueidentifier,
    @AppId          uniqueidentifier = NULL,
    @ErrorCode      nvarchar(100) = NULL,
    @ErrorMessage   nvarchar(1000) = NULL,
    @ErrorDetail    nvarchar(max) = NULL,
    @HttpStatusCode int = NULL,
    @DeadLetter     bit = 0
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    EXEC broker.usp_Delivery_CheckAccess @DeliveryId, @AppId;

    DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @DeadLettered bit, @NextAvailableAt datetime2(3);

    BEGIN TRAN;

    IF NOT EXISTS (SELECT 1 FROM broker.Deliveries WITH (UPDLOCK, ROWLOCK)
                   WHERE DeliveryId = @DeliveryId AND LockToken = @LockToken AND Status = 1 AND LockedUntil > @Now)
    BEGIN
        ROLLBACK;
        THROW 50410, N'The lease was lost: the lock token is stale or the lease has expired.', 1;
    END

    EXEC broker.usp_Delivery_RecordFailure
        @DeliveryId, @Now, 'Nacked', @ErrorCode, @ErrorMessage, @ErrorDetail, @HttpStatusCode, @DeadLetter,
        @DeadLettered OUTPUT, @NextAvailableAt OUTPUT;

    COMMIT;

    SELECT @DeadLettered AS DeadLettered, @NextAvailableAt AS NextAvailableAt,
           (SELECT MessageId FROM broker.Deliveries WHERE DeliveryId = @DeliveryId) AS MessageId;
END
GO

-- Extends the lease by the subscription's lock duration from now (at most 600 seconds).
CREATE OR ALTER PROCEDURE broker.usp_Delivery_Renew
    @DeliveryId bigint,
    @LockToken  uniqueidentifier,
    @AppId      uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    EXEC broker.usp_Delivery_CheckAccess @DeliveryId, @AppId;

    DECLARE @Now datetime2(3) = SYSUTCDATETIME();
    DECLARE @Renewed TABLE (LockedUntil datetime2(3));

    UPDATE d
    SET LockedUntil = DATEADD(second, s.LockDurationSeconds, @Now)
    OUTPUT inserted.LockedUntil INTO @Renewed
    FROM broker.Deliveries d
    JOIN broker.Subscriptions s ON s.SubscriptionId = d.SubscriptionId
    WHERE d.DeliveryId = @DeliveryId
      AND d.LockToken = @LockToken
      AND d.Status = 1
      AND d.LockedUntil > @Now;

    IF NOT EXISTS (SELECT 1 FROM @Renewed)
        THROW 50410, N'The lease was lost: the lock token is stale or the lease has expired.', 1;

    SELECT LockedUntil FROM @Renewed;
END
GO

-- Maintenance: leases whose LockedUntil has passed count as a failed attempt (LeaseExpired) and are
-- retried or dead-lettered. One row per transaction keeps locks short; READPAST skips rows a
-- concurrent ACK is settling, which then wins or loses on LockedUntil.
CREATE OR ALTER PROCEDURE broker.usp_Delivery_ExpireLeases
    @MaxRows int = 500
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    DECLARE @Processed int = 0, @DeliveryId bigint, @Now datetime2(3), @DeadLettered bit, @NextAvailableAt datetime2(3);

    WHILE @Processed < @MaxRows
    BEGIN
        SET @DeliveryId = NULL;
        SET @Now = SYSUTCDATETIME();

        BEGIN TRAN;

        SELECT TOP (1) @DeliveryId = DeliveryId
        FROM broker.Deliveries WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE Status = 1 AND LockedUntil <= @Now
        ORDER BY LockedUntil;

        IF @DeliveryId IS NULL
        BEGIN
            COMMIT;
            BREAK;
        END

        EXEC broker.usp_Delivery_RecordFailure
            @DeliveryId, @Now, 'LeaseExpired', N'LeaseExpired', N'The lease expired before the delivery was settled.',
            NULL, NULL, 0, @DeadLettered OUTPUT, @NextAvailableAt OUTPUT;

        COMMIT;
        SET @Processed += 1;
    END

    SELECT @Processed AS ExpiredCount;
END
GO

-- Maintenance: a Pending delivery past ExpiresAt is never handed out; it is dead-lettered as
-- Expired. A Leased delivery may finish its current attempt.
CREATE OR ALTER PROCEDURE broker.usp_Delivery_ExpirePending
    @MaxRows int = 1000
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    DECLARE @Now datetime2(3) = SYSUTCDATETIME();
    DECLARE @Expired TABLE (DeliveryId bigint PRIMARY KEY, MessageId uniqueidentifier, SubscriptionId uniqueidentifier, AttemptCount int);

    BEGIN TRAN;

    WITH due AS
    (
        SELECT TOP (@MaxRows) DeliveryId, MessageId, SubscriptionId, AttemptCount, Status
        FROM broker.Deliveries WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE Status = 0 AND ExpiresAt <= @Now
    )
    UPDATE due SET Status = 3
    OUTPUT inserted.DeliveryId, inserted.MessageId, inserted.SubscriptionId, inserted.AttemptCount INTO @Expired;

    INSERT broker.DeadLetters
        (DeliveryId, MessageId, SubscriptionId, TopicId, Reason, AttemptCount, LastError, FirstFailureAt, LastFailureAt, DeadLetteredAt)
    SELECT e.DeliveryId, e.MessageId, e.SubscriptionId, s.TopicId, 'Expired', e.AttemptCount,
           N'The delivery expired before it was delivered.',
           COALESCE(f.FirstFailureAt, @Now), @Now, @Now
    FROM @Expired e
    JOIN broker.Subscriptions s ON s.SubscriptionId = e.SubscriptionId
    OUTER APPLY (SELECT MIN(a.EndedAt) AS FirstFailureAt
                 FROM broker.DeliveryAttempts a WITH (FORCESEEK)
                 WHERE a.DeliveryId = e.DeliveryId
                   AND a.Outcome IN ('Nacked', 'LeaseExpired')
                   AND a.LeasedAt >= COALESCE((SELECT MAX(RequeuedAt) FROM broker.DeadLetters r WITH (FORCESEEK) WHERE r.DeliveryId = e.DeliveryId), '0001-01-01')) f;

    COMMIT;

    SELECT COUNT(*) AS ExpiredCount FROM @Expired;
END
GO

-- [Fix 1] Requeue resets AttemptCount (the retry budget) but not TotalAttemptCount, so attempt
-- numbers keep increasing, and closes the open DLQ row instead of deleting it. ExpiresAt is cleared:
-- an operator requeue is an explicit decision to deliver again.
CREATE OR ALTER PROCEDURE broker.usp_DeadLetter_Requeue
    @DeliveryId bigint,
    @RequeuedBy uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @Status tinyint, @SubscriptionStatus varchar(10);

    BEGIN TRAN;

    SELECT @Status = d.Status, @SubscriptionStatus = s.Status
    FROM broker.Deliveries d WITH (UPDLOCK, ROWLOCK)
    JOIN broker.Subscriptions s ON s.SubscriptionId = d.SubscriptionId
    WHERE d.DeliveryId = @DeliveryId;

    IF @Status IS NULL
    BEGIN
        ROLLBACK;
        THROW 50404, N'Delivery was not found.', 1;
    END
    IF @Status <> 3
    BEGIN
        ROLLBACK;
        THROW 50409, N'The delivery is not in the dead letter queue.', 1;
    END
    IF @SubscriptionStatus = 'Deleted'
    BEGIN
        ROLLBACK;
        THROW 50409, N'The subscription has been deleted.', 1;
    END

    UPDATE broker.Deliveries
    SET Status = 0, AttemptCount = 0, AvailableAt = @Now, ExpiresAt = NULL, LockToken = NULL, LockedUntil = NULL
    WHERE DeliveryId = @DeliveryId;

    UPDATE dl SET RequeuedAt = @Now, RequeuedBy = @RequeuedBy
    FROM broker.DeadLetters dl WITH (INDEX (IX_DeadLetters_Delivery))
    WHERE dl.DeliveryId = @DeliveryId AND dl.RequeuedAt IS NULL;

    COMMIT;

    SELECT MessageId FROM broker.Deliveries WHERE DeliveryId = @DeliveryId;
END
GO
