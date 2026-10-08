-- Internal Message Broker, Phase 1 schema.
-- Runs once (journaled by DbUp). Procedures, views and functions live in db/Programmability and are
-- re-applied on every deploy. Targets SQL Server 2019+, so no native json type, LEAST/GREATEST or
-- GENERATE_SERIES.

IF SCHEMA_ID(N'broker') IS NULL EXEC(N'CREATE SCHEMA broker AUTHORIZATION dbo');
GO

CREATE TABLE broker.Applications
(
    AppId      uniqueidentifier NOT NULL CONSTRAINT PK_Applications PRIMARY KEY,
    Name       nvarchar(100) COLLATE Latin1_General_100_CI_AS NOT NULL CONSTRAINT UQ_Applications_Name UNIQUE,
    IsAdmin    bit           NOT NULL CONSTRAINT DF_Applications_IsAdmin DEFAULT (0),
    IsActive   bit           NOT NULL CONSTRAINT DF_Applications_IsActive DEFAULT (1),
    CreatedAt  datetime2(3)  NOT NULL CONSTRAINT DF_Applications_CreatedAt DEFAULT (SYSUTCDATETIME())
);
GO

-- [Fix 6] Keys live in their own table so an application can hold two active keys during rotation.
CREATE TABLE broker.ApiKeys
(
    KeyId      uniqueidentifier NOT NULL CONSTRAINT PK_ApiKeys PRIMARY KEY,
    AppId      uniqueidentifier NOT NULL CONSTRAINT FK_ApiKeys_Applications REFERENCES broker.Applications (AppId),
    Prefix     varchar(32)   NOT NULL CONSTRAINT UQ_ApiKeys_Prefix UNIQUE,
    Hash       varbinary(32) NOT NULL,
    IsActive   bit           NOT NULL CONSTRAINT DF_ApiKeys_IsActive DEFAULT (1),
    CreatedAt  datetime2(3)  NOT NULL CONSTRAINT DF_ApiKeys_CreatedAt DEFAULT (SYSUTCDATETIME()),
    ExpiresAt  datetime2(3)  NULL
);
CREATE INDEX IX_ApiKeys_AppId ON broker.ApiKeys (AppId) WHERE IsActive = 1;
GO

CREATE TABLE broker.AppPermissions
(
    AppId        uniqueidentifier NOT NULL CONSTRAINT FK_AppPermissions_Applications REFERENCES broker.Applications (AppId),
    ResourceType varchar(20)  NOT NULL CONSTRAINT CK_AppPermissions_ResourceType CHECK (ResourceType IN ('Topic', 'Subscription')),
    ResourceId   uniqueidentifier NOT NULL,
    Permission   varchar(20)  NOT NULL CONSTRAINT CK_AppPermissions_Permission CHECK (Permission IN ('Publish', 'Receive', 'Manage')),
    CONSTRAINT PK_AppPermissions PRIMARY KEY (AppId, ResourceType, ResourceId, Permission)
);
GO

CREATE TABLE broker.Topics
(
    TopicId           uniqueidentifier NOT NULL CONSTRAINT PK_Topics PRIMARY KEY,
    Name              nvarchar(100) COLLATE Latin1_General_100_CI_AS NOT NULL,
    DefaultTtlSeconds int          NULL CONSTRAINT CK_Topics_DefaultTtl CHECK (DefaultTtlSeconds > 0),
    IsDeleted         bit          NOT NULL CONSTRAINT DF_Topics_IsDeleted DEFAULT (0),
    CreatedAt         datetime2(3) NOT NULL CONSTRAINT DF_Topics_CreatedAt DEFAULT (SYSUTCDATETIME())
);
CREATE UNIQUE INDEX UQ_Topics_Name ON broker.Topics (Name) WHERE IsDeleted = 0;
GO

CREATE TABLE broker.Subscriptions
(
    SubscriptionId          uniqueidentifier NOT NULL CONSTRAINT PK_Subscriptions PRIMARY KEY,
    TopicId                 uniqueidentifier NOT NULL CONSTRAINT FK_Subscriptions_Topics REFERENCES broker.Topics (TopicId),
    Name                    nvarchar(100) COLLATE Latin1_General_100_CI_AS NOT NULL,
    OwnerAppId              uniqueidentifier NOT NULL CONSTRAINT FK_Subscriptions_Applications REFERENCES broker.Applications (AppId),
    Status                  varchar(10)   NOT NULL CONSTRAINT CK_Subscriptions_Status CHECK (Status IN ('Active', 'Paused', 'Deleted')),
    DeliveryMode            varchar(10)   NOT NULL CONSTRAINT CK_Subscriptions_Mode CHECK (DeliveryMode IN ('Webhook', 'SignalR', 'Pull')),
    WebhookUrl              nvarchar(2000) NULL,
    WebhookSecret           nvarchar(1000) NULL,          -- protected with ASP.NET Core Data Protection
    PreviousWebhookSecret   nvarchar(1000) NULL,          -- [Fix 7] valid until PreviousSecretExpiresAt
    PreviousSecretExpiresAt datetime2(3)  NULL,
    WebhookTimeoutSeconds   int NOT NULL,
    MaxConcurrentDeliveries int NOT NULL,
    MaxAttempts             int NOT NULL CONSTRAINT CK_Subscriptions_MaxAttempts CHECK (MaxAttempts >= 1),
    LockDurationSeconds     int NOT NULL CONSTRAINT CK_Subscriptions_Lock CHECK (LockDurationSeconds BETWEEN 1 AND 600),
    RetryBaseDelaySeconds   int NOT NULL,
    RetryMaxDelaySeconds    int NOT NULL,
    TtlSeconds              int NULL,
    CreatedAt               datetime2(3) NOT NULL CONSTRAINT DF_Subscriptions_CreatedAt DEFAULT (SYSUTCDATETIME()),
    UpdatedAt               datetime2(3) NOT NULL CONSTRAINT DF_Subscriptions_UpdatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_Subscriptions_Retry CHECK (RetryBaseDelaySeconds >= 1 AND RetryMaxDelaySeconds >= RetryBaseDelaySeconds)
);
CREATE UNIQUE INDEX UQ_Subscriptions_TopicName ON broker.Subscriptions (TopicId, Name) WHERE Status <> 'Deleted';
GO

CREATE TABLE broker.Messages
(
    MessageSeq     bigint IDENTITY (1, 1) NOT NULL CONSTRAINT PK_Messages PRIMARY KEY CLUSTERED,
    MessageId      uniqueidentifier NOT NULL CONSTRAINT UQ_Messages_MessageId UNIQUE NONCLUSTERED,
    TopicId        uniqueidentifier NOT NULL CONSTRAINT FK_Messages_Topics REFERENCES broker.Topics (TopicId),
    MessageType    nvarchar(200) NOT NULL,
    CorrelationId  nvarchar(100) NOT NULL,
    PublisherAppId uniqueidentifier NOT NULL CONSTRAINT FK_Messages_Applications REFERENCES broker.Applications (AppId),
    IdempotencyKey nvarchar(100) NULL,
    Payload        nvarchar(max) NOT NULL CONSTRAINT CK_Messages_PayloadJson CHECK (ISJSON(Payload) = 1),       -- [Fix 5]
    Properties     nvarchar(max) NULL CONSTRAINT CK_Messages_PropertiesJson CHECK (Properties IS NULL OR ISJSON(Properties) = 1),
    CreatedAt      datetime2(3) NOT NULL,
    ExpiresAt      datetime2(3) NULL
);
CREATE INDEX IX_Messages_CorrelationId ON broker.Messages (CorrelationId);
CREATE INDEX IX_Messages_CreatedAt ON broker.Messages (CreatedAt);
CREATE UNIQUE INDEX UQ_Messages_Idempotency ON broker.Messages (TopicId, PublisherAppId, IdempotencyKey) WHERE IdempotencyKey IS NOT NULL;
GO

-- The queue. Status: 0 Pending, 1 Leased, 2 Completed, 3 DeadLettered, 4 Cancelled.
CREATE TABLE broker.Deliveries
(
    DeliveryId        bigint IDENTITY (1, 1) NOT NULL CONSTRAINT PK_Deliveries PRIMARY KEY CLUSTERED,
    MessageId         uniqueidentifier NOT NULL CONSTRAINT FK_Deliveries_Messages REFERENCES broker.Messages (MessageId),
    SubscriptionId    uniqueidentifier NOT NULL CONSTRAINT FK_Deliveries_Subscriptions REFERENCES broker.Subscriptions (SubscriptionId),
    Status            tinyint NOT NULL CONSTRAINT CK_Deliveries_Status CHECK (Status BETWEEN 0 AND 4),
    AttemptCount      int NOT NULL CONSTRAINT DF_Deliveries_AttemptCount DEFAULT (0),      -- reset by requeue; compared to MaxAttempts
    TotalAttemptCount int NOT NULL CONSTRAINT DF_Deliveries_TotalAttemptCount DEFAULT (0), -- [Fix 1] never reset; numbers attempts
    AvailableAt       datetime2(3) NOT NULL,
    LockedUntil       datetime2(3) NULL,
    LockToken         uniqueidentifier NULL,
    ExpiresAt         datetime2(3) NULL,
    CreatedAt         datetime2(3) NOT NULL,
    CompletedAt       datetime2(3) NULL,
    CONSTRAINT UQ_Deliveries_MessageSubscription UNIQUE (MessageId, SubscriptionId)
);
CREATE INDEX IX_Deliveries_Lease ON broker.Deliveries (SubscriptionId, Status, AvailableAt) INCLUDE (ExpiresAt);
CREATE INDEX IX_Deliveries_LockedUntil ON broker.Deliveries (LockedUntil) WHERE Status = 1;
CREATE INDEX IX_Deliveries_ExpiresAt ON broker.Deliveries (ExpiresAt) WHERE Status = 0 AND ExpiresAt IS NOT NULL;
CREATE INDEX IX_Deliveries_CompletedAt ON broker.Deliveries (CompletedAt) WHERE Status IN (2, 4);
GO

CREATE TABLE broker.DeliveryAttempts
(
    AttemptId      bigint IDENTITY (1, 1) NOT NULL CONSTRAINT PK_DeliveryAttempts PRIMARY KEY CLUSTERED,
    DeliveryId     bigint NOT NULL CONSTRAINT FK_DeliveryAttempts_Deliveries REFERENCES broker.Deliveries (DeliveryId),
    AttemptNumber  int NOT NULL,
    Channel        varchar(10) NOT NULL CONSTRAINT CK_DeliveryAttempts_Channel CHECK (Channel IN ('Webhook', 'SignalR', 'Pull')),
    LeasedAt       datetime2(3) NOT NULL,
    EndedAt        datetime2(3) NULL,
    Outcome        varchar(20) NULL CONSTRAINT CK_DeliveryAttempts_Outcome CHECK (Outcome IN ('Acked', 'Nacked', 'LeaseExpired', 'Cancelled')),
    HttpStatusCode int NULL,
    DurationMs     int NULL,
    ErrorCode      nvarchar(100) NULL,
    ErrorMessage   nvarchar(1000) NULL,
    ErrorDetail    nvarchar(max) NULL,
    CONSTRAINT UQ_DeliveryAttempts_Number UNIQUE (DeliveryId, AttemptNumber)
);
GO

-- [Fix 1] One row per dead-lettering, so a requeued delivery can be dead-lettered again.
CREATE TABLE broker.DeadLetters
(
    DeadLetterId   bigint IDENTITY (1, 1) NOT NULL CONSTRAINT PK_DeadLetters PRIMARY KEY CLUSTERED,
    DeliveryId     bigint NOT NULL CONSTRAINT FK_DeadLetters_Deliveries REFERENCES broker.Deliveries (DeliveryId),
    MessageId      uniqueidentifier NOT NULL,
    SubscriptionId uniqueidentifier NOT NULL,
    TopicId        uniqueidentifier NOT NULL,
    Reason         varchar(30) NOT NULL CONSTRAINT CK_DeadLetters_Reason CHECK (Reason IN ('MaxAttemptsExceeded', 'Expired', 'RejectedBySubscriber')),
    AttemptCount   int NOT NULL,
    LastError      nvarchar(1000) NULL,
    FirstFailureAt datetime2(3) NULL,
    LastFailureAt  datetime2(3) NULL,
    DeadLetteredAt datetime2(3) NOT NULL,
    RequeuedAt     datetime2(3) NULL,
    RequeuedBy     uniqueidentifier NULL
);
CREATE UNIQUE INDEX UQ_DeadLetters_OpenDelivery ON broker.DeadLetters (DeliveryId) WHERE RequeuedAt IS NULL;
CREATE INDEX IX_DeadLetters_Subscription ON broker.DeadLetters (SubscriptionId, DeadLetteredAt DESC);
GO

-- [Fix 8] Dispatcher heartbeat, read by /health/ready.
CREATE TABLE broker.BrokerHeartbeats
(
    InstanceId nvarchar(200) NOT NULL CONSTRAINT PK_BrokerHeartbeats PRIMARY KEY,
    LastBeatAt datetime2(3)  NOT NULL
);
GO

-- [Fix 9] Webhook host allowlist, maintained by Admins; configuration only seeds it.
CREATE TABLE broker.WebhookAllowedHosts
(
    Host    nvarchar(255) COLLATE Latin1_General_100_CI_AS NOT NULL CONSTRAINT PK_WebhookAllowedHosts PRIMARY KEY,
    AddedBy uniqueidentifier NULL,
    AddedAt datetime2(3) NOT NULL CONSTRAINT DF_WebhookAllowedHosts_AddedAt DEFAULT (SYSUTCDATETIME())
);
GO

CREATE TYPE broker.HostList AS TABLE (Host nvarchar(255) COLLATE Latin1_General_100_CI_AS NOT NULL PRIMARY KEY);
GO
