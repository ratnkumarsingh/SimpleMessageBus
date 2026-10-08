-- Topics and subscriptions.

CREATE OR ALTER PROCEDURE broker.usp_Topic_Create
    @TopicId           uniqueidentifier,
    @Name              nvarchar(100),
    @DefaultTtlSeconds int = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    IF EXISTS (SELECT 1 FROM broker.Topics WHERE Name = @Name AND IsDeleted = 0)
        THROW 50409, N'A topic with this name already exists.', 1;

    INSERT broker.Topics (TopicId, Name, DefaultTtlSeconds) VALUES (@TopicId, @Name, @DefaultTtlSeconds);

    SELECT TopicId, Name, DefaultTtlSeconds, CreatedAt FROM broker.Topics WHERE TopicId = @TopicId;
END
GO

CREATE OR ALTER PROCEDURE broker.usp_Topic_Get
    @TopicId uniqueidentifier = NULL,
    @Name    nvarchar(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TopicId, Name, DefaultTtlSeconds, CreatedAt
    FROM broker.Topics
    WHERE IsDeleted = 0
      AND (TopicId = @TopicId OR (@TopicId IS NULL AND Name = @Name));
END
GO

CREATE OR ALTER PROCEDURE broker.usp_Topic_List
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TopicId, Name, DefaultTtlSeconds, CreatedAt FROM broker.Topics WHERE IsDeleted = 0 ORDER BY Name;
END
GO

CREATE OR ALTER PROCEDURE broker.usp_Topic_Update
    @TopicId           uniqueidentifier,
    @DefaultTtlSeconds int = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    UPDATE broker.Topics SET DefaultTtlSeconds = @DefaultTtlSeconds WHERE TopicId = @TopicId AND IsDeleted = 0;
    IF @@ROWCOUNT = 0
        THROW 50404, N'Topic was not found.', 1;

    SELECT TopicId, Name, DefaultTtlSeconds, CreatedAt FROM broker.Topics WHERE TopicId = @TopicId;
END
GO

-- Spec section 6: a topic can be deleted only when it has no subscriptions. Deletion is soft so
-- message history keeps its foreign key.
CREATE OR ALTER PROCEDURE broker.usp_Topic_Delete
    @TopicId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    BEGIN TRAN;

    IF NOT EXISTS (SELECT 1 FROM broker.Topics WITH (UPDLOCK, HOLDLOCK) WHERE TopicId = @TopicId AND IsDeleted = 0)
    BEGIN
        ROLLBACK;
        THROW 50404, N'Topic was not found.', 1;
    END

    IF EXISTS (SELECT 1 FROM broker.Subscriptions WHERE TopicId = @TopicId AND Status <> 'Deleted')
    BEGIN
        ROLLBACK;
        THROW 50409, N'The topic still has subscriptions. Delete them first.', 1;
    END

    UPDATE broker.Topics SET IsDeleted = 1 WHERE TopicId = @TopicId;
    DELETE broker.AppPermissions WHERE ResourceType = 'Topic' AND ResourceId = @TopicId;

    COMMIT;
END
GO

CREATE OR ALTER PROCEDURE broker.usp_Subscription_Create
    @SubscriptionId          uniqueidentifier,
    @TopicId                 uniqueidentifier,
    @Name                    nvarchar(100),
    @OwnerAppId              uniqueidentifier,
    @DeliveryMode            varchar(10),
    @WebhookUrl              nvarchar(2000) = NULL,
    @WebhookSecret           nvarchar(1000) = NULL,
    @WebhookTimeoutSeconds   int,
    @MaxConcurrentDeliveries int,
    @MaxAttempts             int,
    @LockDurationSeconds     int,
    @RetryBaseDelaySeconds   int,
    @RetryMaxDelaySeconds    int,
    @TtlSeconds              int = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM broker.Topics WHERE TopicId = @TopicId AND IsDeleted = 0)
        THROW 50404, N'Topic was not found.', 1;
    IF NOT EXISTS (SELECT 1 FROM broker.Applications WHERE AppId = @OwnerAppId)
        THROW 50404, N'Owner application was not found.', 1;
    IF EXISTS (SELECT 1 FROM broker.Subscriptions WHERE TopicId = @TopicId AND Name = @Name AND Status <> 'Deleted')
        THROW 50409, N'A subscription with this name already exists on the topic.', 1;

    BEGIN TRAN;

    INSERT broker.Subscriptions
        (SubscriptionId, TopicId, Name, OwnerAppId, Status, DeliveryMode, WebhookUrl, WebhookSecret,
         WebhookTimeoutSeconds, MaxConcurrentDeliveries, MaxAttempts, LockDurationSeconds,
         RetryBaseDelaySeconds, RetryMaxDelaySeconds, TtlSeconds)
    VALUES
        (@SubscriptionId, @TopicId, @Name, @OwnerAppId, 'Active', @DeliveryMode, @WebhookUrl, @WebhookSecret,
         @WebhookTimeoutSeconds, @MaxConcurrentDeliveries, @MaxAttempts, @LockDurationSeconds,
         @RetryBaseDelaySeconds, @RetryMaxDelaySeconds, @TtlSeconds);

    -- The owning application can receive from its own subscription.
    INSERT broker.AppPermissions (AppId, ResourceType, ResourceId, Permission)
    VALUES (@OwnerAppId, 'Subscription', @SubscriptionId, 'Receive');

    COMMIT;

    SELECT * FROM broker.vw_SubscriptionDetails WHERE SubscriptionId = @SubscriptionId;
END
GO

CREATE OR ALTER PROCEDURE broker.usp_Subscription_Get
    @SubscriptionId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT * FROM broker.vw_SubscriptionDetails WHERE SubscriptionId = @SubscriptionId AND Status <> 'Deleted';
END
GO

CREATE OR ALTER PROCEDURE broker.usp_Subscription_List
    @TopicId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM broker.Topics WHERE TopicId = @TopicId AND IsDeleted = 0)
        THROW 50404, N'Topic was not found.', 1;

    SELECT * FROM broker.vw_SubscriptionDetails WHERE TopicId = @TopicId AND Status <> 'Deleted' ORDER BY Name;
END
GO

-- Full replacement of the changeable settings. The service merges PATCH input and validates it first.
-- The delivery mode cannot change; create a new subscription instead.
CREATE OR ALTER PROCEDURE broker.usp_Subscription_Update
    @SubscriptionId          uniqueidentifier,
    @Status                  varchar(10),
    @WebhookUrl              nvarchar(2000) = NULL,
    @WebhookTimeoutSeconds   int,
    @MaxConcurrentDeliveries int,
    @MaxAttempts             int,
    @LockDurationSeconds     int,
    @RetryBaseDelaySeconds   int,
    @RetryMaxDelaySeconds    int,
    @TtlSeconds              int = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    IF @Status NOT IN ('Active', 'Paused')
        THROW 50400, N'Status must be Active or Paused.', 1;

    UPDATE broker.Subscriptions
    SET Status = @Status,
        WebhookUrl = @WebhookUrl,
        WebhookTimeoutSeconds = @WebhookTimeoutSeconds,
        MaxConcurrentDeliveries = @MaxConcurrentDeliveries,
        MaxAttempts = @MaxAttempts,
        LockDurationSeconds = @LockDurationSeconds,
        RetryBaseDelaySeconds = @RetryBaseDelaySeconds,
        RetryMaxDelaySeconds = @RetryMaxDelaySeconds,
        TtlSeconds = @TtlSeconds,
        UpdatedAt = SYSUTCDATETIME()
    WHERE SubscriptionId = @SubscriptionId AND Status <> 'Deleted';

    IF @@ROWCOUNT = 0
        THROW 50404, N'Subscription was not found.', 1;

    SELECT * FROM broker.vw_SubscriptionDetails WHERE SubscriptionId = @SubscriptionId;
END
GO

-- Soft delete. Pending and Leased deliveries become Cancelled (terminal); an open attempt is closed
-- as Cancelled, so a late ACK with the old lock token gets 410.
CREATE OR ALTER PROCEDURE broker.usp_Subscription_Delete
    @SubscriptionId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    DECLARE @Now datetime2(3) = SYSUTCDATETIME();

    BEGIN TRAN;

    UPDATE broker.Subscriptions SET Status = 'Deleted', UpdatedAt = @Now
    WHERE SubscriptionId = @SubscriptionId AND Status <> 'Deleted';

    IF @@ROWCOUNT = 0
    BEGIN
        ROLLBACK;
        THROW 50404, N'Subscription was not found.', 1;
    END

    UPDATE a SET EndedAt = @Now, Outcome = 'Cancelled', DurationMs = DATEDIFF(millisecond, a.LeasedAt, @Now)
    FROM broker.DeliveryAttempts a
    JOIN broker.Deliveries d ON d.DeliveryId = a.DeliveryId
    WHERE d.SubscriptionId = @SubscriptionId AND d.Status = 1 AND a.EndedAt IS NULL;

    UPDATE broker.Deliveries
    SET Status = 4, CompletedAt = @Now, LockToken = NULL, LockedUntil = NULL
    WHERE SubscriptionId = @SubscriptionId AND Status IN (0, 1);

    DELETE broker.AppPermissions WHERE ResourceType = 'Subscription' AND ResourceId = @SubscriptionId;

    COMMIT;
END
GO

-- [Fix 7] The current secret becomes the previous one and stays valid for 24 hours; during that
-- window the webhook sender signs with both.
CREATE OR ALTER PROCEDURE broker.usp_Subscription_RotateSecret
    @SubscriptionId uniqueidentifier,
    @NewSecret      nvarchar(1000)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    DECLARE @Mode varchar(10) = (SELECT DeliveryMode FROM broker.Subscriptions
                                 WHERE SubscriptionId = @SubscriptionId AND Status <> 'Deleted');
    IF @Mode IS NULL
        THROW 50404, N'Subscription was not found.', 1;
    IF @Mode <> 'Webhook'
        THROW 50400, N'Only Webhook subscriptions have a signing secret.', 1;

    UPDATE broker.Subscriptions
    SET PreviousWebhookSecret = WebhookSecret,
        PreviousSecretExpiresAt = DATEADD(hour, 24, SYSUTCDATETIME()),
        WebhookSecret = @NewSecret,
        UpdatedAt = SYSUTCDATETIME()
    WHERE SubscriptionId = @SubscriptionId;

    SELECT * FROM broker.vw_SubscriptionDetails WHERE SubscriptionId = @SubscriptionId;
END
GO

-- Push subscriptions for the lease loop.
CREATE OR ALTER PROCEDURE broker.usp_Subscription_ListActivePush
AS
BEGIN
    SET NOCOUNT ON;
    SELECT * FROM broker.vw_ActivePushSubscriptions;
END
GO
