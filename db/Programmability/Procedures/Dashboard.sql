-- Admin dashboard queries. Admin only: DashboardService checks the caller before calling these.

-- Broker-wide overview: totals, a per-minute throughput series over the last @WindowMinutes, one row
-- per subscription (never its secrets) and the dispatcher heartbeat. Four result sets.
CREATE OR ALTER PROCEDURE broker.usp_Admin_GetOverview
    @WindowMinutes int = 60
AS
BEGIN
    SET NOCOUNT ON;

    IF @WindowMinutes IS NULL OR @WindowMinutes NOT BETWEEN 5 AND 1440
        THROW 50400, N'windowMinutes must be between 5 and 1440.', 1;

    DECLARE @Now datetime2(3) = SYSUTCDATETIME();
    -- The series starts on a whole minute so every bucket is a full calendar minute except the current one.
    DECLARE @Start datetime2(3) = DATEADD(minute, DATEDIFF(minute, '2000-01-01', @Now) - (@WindowMinutes - 1), CAST('2000-01-01' AS datetime2(3)));

    SELECT
        (SELECT COUNT_BIG(*) FROM broker.Deliveries WHERE Status = 0) AS PendingCount,
        (SELECT COUNT_BIG(*) FROM broker.Deliveries WHERE Status = 1) AS LeasedCount,
        -- Only live subscriptions: a deleted one's DLQ entries can no longer be requeued.
        (SELECT COUNT_BIG(*) FROM broker.DeadLetters dl
         JOIN broker.Subscriptions s ON s.SubscriptionId = dl.SubscriptionId
         WHERE dl.RequeuedAt IS NULL AND s.Status <> 'Deleted') AS DeadLetteredCount,
        (SELECT COUNT_BIG(*) FROM broker.Messages WHERE CreatedAt >= @Start) AS PublishedInWindow,
        (SELECT COUNT_BIG(*) FROM broker.Deliveries WHERE Status = 2 AND CompletedAt >= @Start) AS CompletedInWindow,
        @WindowMinutes AS WindowMinutes,
        @Now AS GeneratedAt;

    WITH minutes AS
    (
        SELECT TOP (@WindowMinutes) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS N
        FROM sys.all_objects
    ),
    published AS
    (
        SELECT DATEDIFF(minute, @Start, CreatedAt) AS N, COUNT(*) AS C
        FROM broker.Messages WHERE CreatedAt >= @Start GROUP BY DATEDIFF(minute, @Start, CreatedAt)
    ),
    completed AS
    (
        SELECT DATEDIFF(minute, @Start, CompletedAt) AS N, COUNT(*) AS C
        FROM broker.Deliveries WHERE Status = 2 AND CompletedAt >= @Start
        GROUP BY DATEDIFF(minute, @Start, CompletedAt)
    ),
    failed AS
    (
        SELECT DATEDIFF(minute, @Start, EndedAt) AS N, COUNT(*) AS C
        FROM broker.DeliveryAttempts WHERE EndedAt >= @Start AND Outcome IN ('Nacked', 'LeaseExpired')
        GROUP BY DATEDIFF(minute, @Start, EndedAt)
    ),
    deadLettered AS
    (
        SELECT DATEDIFF(minute, @Start, DeadLetteredAt) AS N, COUNT(*) AS C
        FROM broker.DeadLetters WHERE DeadLetteredAt >= @Start GROUP BY DATEDIFF(minute, @Start, DeadLetteredAt)
    )
    SELECT DATEADD(minute, m.N, @Start) AS Minute,
           COALESCE(p.C, 0) AS Published,
           COALESCE(c.C, 0) AS Completed,
           COALESCE(f.C, 0) AS Failed,
           COALESCE(d.C, 0) AS DeadLettered
    FROM minutes m
    LEFT JOIN published p ON p.N = m.N
    LEFT JOIN completed c ON c.N = m.N
    LEFT JOIN failed f ON f.N = m.N
    LEFT JOIN deadLettered d ON d.N = m.N
    ORDER BY m.N;

    SELECT s.SubscriptionId, s.Name, s.TopicId, s.TopicName, s.OwnerAppId, s.Status, s.DeliveryMode,
           s.PendingCount, s.LeasedCount, s.DeadLetteredCount
    FROM broker.vw_SubscriptionDetails s
    WHERE s.Status <> 'Deleted'
    ORDER BY s.TopicName, s.Name;

    SELECT MAX(LastBeatAt) AS LastBeatAt, @Now AS DbNow FROM broker.BrokerHeartbeats;
END
GO

-- Messages newest first, filtered, with keyset paging: pass the smallest MessageSeq of the previous
-- page as @BeforeSeq. The status is derived as in vw_MessageStatus, per message on the page.
CREATE OR ALTER PROCEDURE broker.usp_Admin_Message_Search
    @TopicId        uniqueidentifier = NULL,
    @Status         varchar(30) = NULL,
    @MessageType    nvarchar(200) = NULL,
    @CorrelationId  nvarchar(100) = NULL,
    @PublisherAppId uniqueidentifier = NULL,
    @From           datetime2(3) = NULL,
    @To             datetime2(3) = NULL,
    @BeforeSeq      bigint = NULL,
    @PageSize       int = 50
AS
BEGIN
    SET NOCOUNT ON;

    IF @PageSize IS NULL OR @PageSize NOT BETWEEN 1 AND 200
        THROW 50400, N'pageSize must be between 1 and 200.', 1;
    IF @From IS NOT NULL AND @To IS NOT NULL AND @From > @To
        THROW 50400, N'from must not be later than to.', 1;
    IF @Status IS NOT NULL AND @Status NOT IN ('InProgress', 'Completed', 'PartiallyDeadLettered', 'DeadLettered')
        THROW 50400, N'status must be InProgress, Completed, PartiallyDeadLettered or DeadLettered.', 1;

    SELECT TOP (@PageSize)
           m.MessageSeq, m.MessageId, t.Name AS TopicName, m.MessageType, m.CorrelationId, m.PublisherAppId,
           a.Name AS PublisherName, m.CreatedAt, m.ExpiresAt, st.Status, st.DeliveryCount,
           st.PendingCount, st.LeasedCount, st.CompletedCount, st.DeadLetteredCount
    FROM broker.Messages m
    JOIN broker.Topics t ON t.TopicId = m.TopicId
    JOIN broker.Applications a ON a.AppId = m.PublisherAppId
    CROSS APPLY
    (
        SELECT
            CASE
                WHEN SUM(CASE WHEN d.Status IN (0, 1) THEN 1 ELSE 0 END) > 0 THEN 'InProgress'
                WHEN COALESCE(SUM(CASE WHEN d.Status = 3 THEN 1 ELSE 0 END), 0) = 0 THEN 'Completed'
                WHEN SUM(CASE WHEN d.Status = 3 THEN 1 ELSE 0 END) = SUM(CASE WHEN d.Status <> 4 THEN 1 ELSE 0 END) THEN 'DeadLettered'
                ELSE 'PartiallyDeadLettered'
            END AS Status,
            COUNT(d.DeliveryId) AS DeliveryCount,
            COALESCE(SUM(CASE WHEN d.Status = 0 THEN 1 ELSE 0 END), 0) AS PendingCount,
            COALESCE(SUM(CASE WHEN d.Status = 1 THEN 1 ELSE 0 END), 0) AS LeasedCount,
            COALESCE(SUM(CASE WHEN d.Status = 2 THEN 1 ELSE 0 END), 0) AS CompletedCount,
            COALESCE(SUM(CASE WHEN d.Status = 3 THEN 1 ELSE 0 END), 0) AS DeadLetteredCount
        FROM broker.Deliveries d
        WHERE d.MessageId = m.MessageId
    ) st
    WHERE (@TopicId IS NULL OR m.TopicId = @TopicId)
      AND (@MessageType IS NULL OR m.MessageType = @MessageType)
      AND (@CorrelationId IS NULL OR m.CorrelationId = @CorrelationId)
      AND (@PublisherAppId IS NULL OR m.PublisherAppId = @PublisherAppId)
      AND (@From IS NULL OR m.CreatedAt >= @From)
      AND (@To IS NULL OR m.CreatedAt <= @To)
      AND (@BeforeSeq IS NULL OR m.MessageSeq < @BeforeSeq)
      AND (@Status IS NULL OR st.Status = @Status)
    ORDER BY m.MessageSeq DESC
    OPTION (RECOMPILE);
END
GO

-- Broker-wide DLQ, newest first; keyset paging on DeadLetterId like usp_DeadLetter_List. The payload
-- is left out: the message detail page shows it. Entries of deleted subscriptions are left out too:
-- they cannot be requeued (usp_DeadLetter_Requeue refuses them), so there is nothing to act on.
CREATE OR ALTER PROCEDURE broker.usp_Admin_DeadLetter_Search
    @TopicId         uniqueidentifier = NULL,
    @SubscriptionId  uniqueidentifier = NULL,
    @Reason          varchar(30) = NULL,
    @From            datetime2(3) = NULL,
    @To              datetime2(3) = NULL,
    @IncludeRequeued bit = 0,
    @BeforeId        bigint = NULL,
    @PageSize        int = 50
AS
BEGIN
    SET NOCOUNT ON;

    IF @PageSize IS NULL OR @PageSize NOT BETWEEN 1 AND 200
        THROW 50400, N'pageSize must be between 1 and 200.', 1;
    IF @From IS NOT NULL AND @To IS NOT NULL AND @From > @To
        THROW 50400, N'from must not be later than to.', 1;
    IF @Reason IS NOT NULL AND @Reason NOT IN ('MaxAttemptsExceeded', 'Expired', 'RejectedBySubscriber')
        THROW 50400, N'reason must be MaxAttemptsExceeded, Expired or RejectedBySubscriber.', 1;

    SELECT TOP (@PageSize)
           DeadLetterId, DeliveryId, MessageId, SubscriptionId, SubscriptionName, TopicId, TopicName, Reason,
           AttemptCount, LastError, FirstFailureAt, LastFailureAt, DeadLetteredAt, RequeuedAt, RequeuedBy,
           MessageType, CorrelationId, MessageCreatedAt
    FROM broker.vw_DeadLetterDetails d
    WHERE EXISTS (SELECT 1 FROM broker.Subscriptions s WHERE s.SubscriptionId = d.SubscriptionId AND s.Status <> 'Deleted')
      AND (@TopicId IS NULL OR TopicId = @TopicId)
      AND (@SubscriptionId IS NULL OR SubscriptionId = @SubscriptionId)
      AND (@Reason IS NULL OR Reason = @Reason)
      AND (@From IS NULL OR DeadLetteredAt >= @From)
      AND (@To IS NULL OR DeadLetteredAt <= @To)
      AND (@IncludeRequeued = 1 OR RequeuedAt IS NULL)
      AND (@BeforeId IS NULL OR DeadLetterId < @BeforeId)
    ORDER BY DeadLetterId DESC
    OPTION (RECOMPILE);
END
GO
