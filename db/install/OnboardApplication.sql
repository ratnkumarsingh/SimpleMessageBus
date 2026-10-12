-- Internal Message Broker: onboard one application.
--
-- Creates the application, issues its API key, lets it publish to topics, and creates the Pull or SignalR
-- subscriptions it receives from. Topics that do not exist yet are created. Run it after BrokerDb_Full.sql,
-- in SSMS or with: sqlcmd -b -S <server> -i OnboardApplication.sql
--
-- Running it again with the same values changes nothing: the application, its key, its permissions and
-- its subscriptions are kept, and a new key is issued only when the application has no active key.
--
-- Webhook subscriptions cannot be created here: the broker encrypts their signing secret with its own
-- Data Protection keys. Create those with the API (POST /api/v1/topics/{topicId}/subscriptions) or the
-- dashboard, using an Admin key.

USE [BrokerDb];   -- the broker database
GO

SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

-- ===== Settings: edit these values before running

DECLARE @AppName nvarchar(100) = N'orders-service';  -- letters, digits, '.', '_' and '-'
DECLARE @IsAdmin bit = 0;                            -- 1 only for operators and tools that manage the broker
DECLARE @ApiKey varchar(200) = '';                   -- empty: generate a key and show it once, at the end

-- Topics the application publishes to.
DECLARE @PublishTo TABLE (Topic nvarchar(100) COLLATE Latin1_General_100_CI_AS NOT NULL PRIMARY KEY);
INSERT @PublishTo (Topic) VALUES
    (N'orders');

-- Subscriptions the application receives from. DeliveryMode is Pull or SignalR. The name must be unique
-- on its topic; prefixing it with the application name keeps it so.
DECLARE @Subscriptions TABLE (Topic nvarchar(100) COLLATE Latin1_General_100_CI_AS NOT NULL, Name nvarchar(100) COLLATE Latin1_General_100_CI_AS NOT NULL,
                              DeliveryMode varchar(10) NOT NULL,
                              PRIMARY KEY (Topic, Name));
INSERT @Subscriptions (Topic, Name, DeliveryMode) VALUES
    (N'payments', N'orders-service-payments', 'Pull');

-- Settings for new topics and subscriptions. These match the broker's defaults (Broker:Defaults).
DECLARE @TopicDefaultTtlSeconds int = NULL;          -- NULL: messages do not expire unless the publisher says so
DECLARE @MaxAttempts int = 4;
DECLARE @LockDurationSeconds int = 60;
DECLARE @RetryBaseDelaySeconds int = 30;
DECLARE @RetryMaxDelaySeconds int = 900;
DECLARE @MaxConcurrentDeliveries int = 8;
DECLARE @WebhookTimeoutSeconds int = 30;             -- stored with every subscription; used only by webhooks

-- ===== Checks, before anything changes

SET @AppName = LTRIM(RTRIM(@AppName));
SET @ApiKey = LTRIM(RTRIM(@ApiKey));
UPDATE @PublishTo SET Topic = LTRIM(RTRIM(Topic));
UPDATE @Subscriptions SET Topic = LTRIM(RTRIM(Topic)), Name = LTRIM(RTRIM(Name));

IF @AppName = N'' OR @AppName COLLATE Latin1_General_BIN2 LIKE N'%[^-A-Za-z0-9._]%'
    THROW 50400, N'@AppName must be 1-100 letters, digits, ''.'', ''_'' and ''-''.', 1;
IF EXISTS (SELECT 1 FROM @PublishTo WHERE Topic = N'' OR Topic COLLATE Latin1_General_BIN2 LIKE N'%[^-A-Za-z0-9._]%')
    OR EXISTS (SELECT 1 FROM @Subscriptions WHERE Topic = N'' OR Topic COLLATE Latin1_General_BIN2 LIKE N'%[^-A-Za-z0-9._]%'
                                               OR Name = N'' OR Name COLLATE Latin1_General_BIN2 LIKE N'%[^-A-Za-z0-9._]%')
    THROW 50400, N'Topic and subscription names must be 1-100 letters, digits, ''.'', ''_'' and ''-''.', 1;
IF EXISTS (SELECT 1 FROM @Subscriptions WHERE DeliveryMode = 'Webhook')
    THROW 50400, N'Webhook subscriptions cannot be created from SQL. Create them with the API or the dashboard.', 1;
IF EXISTS (SELECT 1 FROM @Subscriptions WHERE DeliveryMode NOT IN ('Pull', 'SignalR'))
    THROW 50400, N'DeliveryMode must be Pull or SignalR.', 1;
IF @ApiKey <> '' AND (LEN(@ApiKey) < 18 OR LEFT(@ApiKey, 4) COLLATE Latin1_General_BIN2 <> 'mbk_' OR SUBSTRING(@ApiKey, 17, 1) <> '_')
    THROW 50400, N'@ApiKey is not a broker API key. Leave it empty to generate one.', 1;
-- The same limits the API enforces (SubscriptionSettingsValidator).
IF @LockDurationSeconds NOT BETWEEN 1 AND 600 OR @MaxAttempts NOT BETWEEN 1 AND 100
   OR @MaxConcurrentDeliveries NOT BETWEEN 1 AND 256 OR @RetryBaseDelaySeconds < 1
   OR @RetryMaxDelaySeconds NOT BETWEEN @RetryBaseDelaySeconds AND 86400 OR @TopicDefaultTtlSeconds <= 0
    THROW 50400, N'A subscription or topic setting is out of range.', 1;

-- ===== Onboard

DECLARE @AppId uniqueidentifier, @Prefix varchar(32), @Hash varbinary(32), @NewKey varchar(100), @KeyId uniqueidentifier;
DECLARE @App TABLE (AppId uniqueidentifier, Name nvarchar(100), IsAdmin bit, IsActive bit, CreatedAt datetime2(3));
DECLARE @Key TABLE (KeyId uniqueidentifier, AppId uniqueidentifier, Prefix varchar(32), IsActive bit, CreatedAt datetime2(3), ExpiresAt datetime2(3));
DECLARE @Topic TABLE (TopicId uniqueidentifier, Name nvarchar(100), DefaultTtlSeconds int, CreatedAt datetime2(3));
DROP TABLE IF EXISTS #CreatedSubscription;
SELECT TOP (0) * INTO #CreatedSubscription FROM broker.vw_SubscriptionDetails;

BEGIN TRANSACTION;

-- 1. The application.
SELECT @AppId = AppId FROM broker.Applications WHERE Name = @AppName;
IF @AppId IS NULL
BEGIN
    SET @AppId = NEWID();
    INSERT @App EXEC broker.usp_Application_Create @AppId, @AppName, @IsAdmin;
    PRINT N'Created application ' + @AppName;
END
ELSE IF EXISTS (SELECT 1 FROM broker.Applications WHERE AppId = @AppId AND IsAdmin <> @IsAdmin)
    THROW 50409, N'The application exists with a different @IsAdmin. Change it with the API.', 1;

-- 2. Its API key.
IF @ApiKey <> ''
BEGIN
    SELECT @Prefix = LEFT(@ApiKey, 16), @Hash = HASHBYTES('SHA2_256', @ApiKey);
    IF EXISTS (SELECT 1 FROM broker.ApiKeys WHERE Prefix = @Prefix AND AppId <> @AppId)
        THROW 50409, N'@ApiKey already belongs to another application.', 1;
    IF EXISTS (SELECT 1 FROM broker.ApiKeys WHERE Prefix = @Prefix)
        SET @Prefix = NULL;   -- already issued to this application
END
ELSE IF NOT EXISTS (SELECT 1 FROM broker.ApiKeys
                    WHERE AppId = @AppId AND IsActive = 1 AND (ExpiresAt IS NULL OR ExpiresAt > SYSUTCDATETIME()))
    EXEC broker.usp_ApiKey_Generate @NewKey OUTPUT, @Prefix OUTPUT, @Hash OUTPUT;

IF @Prefix IS NOT NULL
BEGIN
    IF (SELECT COUNT(*) FROM broker.ApiKeys
        WHERE AppId = @AppId AND IsActive = 1 AND (ExpiresAt IS NULL OR ExpiresAt > SYSUTCDATETIME())) >= 2
        THROW 50409, N'The application already has two active keys. Deactivate one with the API first.', 1;
    SET @KeyId = NEWID();
    INSERT @Key EXEC broker.usp_ApiKey_Create @KeyId, @AppId, @Prefix, @Hash;
END

-- 3. Topics it uses that do not exist yet.
DECLARE @Name nvarchar(100), @TopicId uniqueidentifier;
DECLARE @NewTopics TABLE (Name nvarchar(100) COLLATE Latin1_General_100_CI_AS PRIMARY KEY);
INSERT @NewTopics (Name)
SELECT Topic FROM @PublishTo UNION SELECT Topic FROM @Subscriptions
EXCEPT SELECT Name FROM broker.Topics WHERE IsDeleted = 0;

WHILE EXISTS (SELECT 1 FROM @NewTopics)
BEGIN
    SELECT TOP (1) @Name = Name FROM @NewTopics ORDER BY Name;
    SET @TopicId = NEWID();
    INSERT @Topic EXEC broker.usp_Topic_Create @TopicId, @Name, @TopicDefaultTtlSeconds;
    PRINT N'Created topic ' + @Name;
    DELETE @NewTopics WHERE Name = @Name;
END

-- 4. Publish permissions.
DECLARE @Grants TABLE (TopicId uniqueidentifier PRIMARY KEY);
INSERT @Grants (TopicId)
SELECT t.TopicId FROM @PublishTo p JOIN broker.Topics t ON t.Name = p.Topic AND t.IsDeleted = 0;

WHILE EXISTS (SELECT 1 FROM @Grants)
BEGIN
    SELECT TOP (1) @TopicId = TopicId FROM @Grants;
    EXEC broker.usp_Permission_Grant @AppId, 'Topic', @TopicId, 'Publish';
    DELETE @Grants WHERE TopicId = @TopicId;
END

-- 5. Subscriptions. The owner gets Receive on each one.
IF EXISTS (SELECT 1 FROM @Subscriptions s
           JOIN broker.Topics t ON t.Name = s.Topic AND t.IsDeleted = 0
           JOIN broker.Subscriptions x ON x.TopicId = t.TopicId AND x.Name = s.Name AND x.Status <> 'Deleted'
           WHERE x.OwnerAppId <> @AppId OR x.DeliveryMode <> s.DeliveryMode)
    THROW 50409, N'A subscription with one of these names exists on its topic, owned by another application or with another DeliveryMode.', 1;

DECLARE @Pending TABLE (TopicId uniqueidentifier, Name nvarchar(100) COLLATE Latin1_General_100_CI_AS, DeliveryMode varchar(10), PRIMARY KEY (TopicId, Name));
INSERT @Pending (TopicId, Name, DeliveryMode)
SELECT t.TopicId, s.Name, s.DeliveryMode
FROM @Subscriptions s JOIN broker.Topics t ON t.Name = s.Topic AND t.IsDeleted = 0
WHERE NOT EXISTS (SELECT 1 FROM broker.Subscriptions x WHERE x.TopicId = t.TopicId AND x.Name = s.Name AND x.Status <> 'Deleted');

DECLARE @SubscriptionId uniqueidentifier, @Mode varchar(10);
WHILE EXISTS (SELECT 1 FROM @Pending)
BEGIN
    SELECT TOP (1) @TopicId = TopicId, @Name = Name, @Mode = DeliveryMode FROM @Pending ORDER BY Name;
    SET @SubscriptionId = NEWID();
    INSERT #CreatedSubscription
    EXEC broker.usp_Subscription_Create
        @SubscriptionId = @SubscriptionId, @TopicId = @TopicId, @Name = @Name, @OwnerAppId = @AppId,
        @DeliveryMode = @Mode, @WebhookTimeoutSeconds = @WebhookTimeoutSeconds,
        @MaxConcurrentDeliveries = @MaxConcurrentDeliveries, @MaxAttempts = @MaxAttempts,
        @LockDurationSeconds = @LockDurationSeconds, @RetryBaseDelaySeconds = @RetryBaseDelaySeconds,
        @RetryMaxDelaySeconds = @RetryMaxDelaySeconds;
    PRINT N'Created subscription ' + @Name;
    DELETE @Pending WHERE TopicId = @TopicId AND Name = @Name;
END

COMMIT TRANSACTION;
DROP TABLE #CreatedSubscription;

-- ===== Result

IF @NewKey IS NOT NULL
    PRINT N'New API key for ' + @AppName + N', shown only now: ' + @NewKey;

SELECT a.Name AS Application, a.AppId, a.IsAdmin, k.Prefix AS KeyPrefix,
       CASE WHEN k.Prefix = LEFT(@NewKey, 16) THEN @NewKey END AS NewApiKey
FROM broker.Applications a
LEFT JOIN broker.ApiKeys k ON k.AppId = a.AppId AND k.IsActive = 1
WHERE a.AppId = @AppId;

SELECT p.Permission, p.ResourceType, COALESCE(t.Name, st.Name + N'/' + s.Name) AS Resource, s.DeliveryMode, p.ResourceId
FROM broker.AppPermissions p
LEFT JOIN broker.Topics t ON p.ResourceType = 'Topic' AND t.TopicId = p.ResourceId
LEFT JOIN broker.Subscriptions s ON p.ResourceType = 'Subscription' AND s.SubscriptionId = p.ResourceId
LEFT JOIN broker.Topics st ON st.TopicId = s.TopicId
WHERE p.AppId = @AppId
ORDER BY p.ResourceType, Resource;
GO
