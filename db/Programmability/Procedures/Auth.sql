-- Applications, API keys and permissions.
-- Error numbers: 50400 validation, 50403 forbidden, 50404 not found, 50409 conflict, 50410 lease lost.

CREATE OR ALTER PROCEDURE broker.usp_Application_Create
    @AppId   uniqueidentifier,
    @Name    nvarchar(100),
    @IsAdmin bit
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    IF EXISTS (SELECT 1 FROM broker.Applications WHERE Name = @Name)
        THROW 50409, N'An application with this name already exists.', 1;

    INSERT broker.Applications (AppId, Name, IsAdmin) VALUES (@AppId, @Name, @IsAdmin);

    SELECT AppId, Name, IsAdmin, IsActive, CreatedAt FROM broker.Applications WHERE AppId = @AppId;
END
GO

CREATE OR ALTER PROCEDURE broker.usp_Application_Get
    @AppId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT AppId, Name, IsAdmin, IsActive, CreatedAt FROM broker.Applications WHERE AppId = @AppId;
END
GO

CREATE OR ALTER PROCEDURE broker.usp_Application_List
AS
BEGIN
    SET NOCOUNT ON;
    SELECT AppId, Name, IsAdmin, IsActive, CreatedAt FROM broker.Applications ORDER BY Name;
END
GO

CREATE OR ALTER PROCEDURE broker.usp_Application_SetActive
    @AppId    uniqueidentifier,
    @IsActive bit
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    UPDATE broker.Applications SET IsActive = @IsActive WHERE AppId = @AppId;
    IF @@ROWCOUNT = 0
        THROW 50404, N'Application was not found.', 1;
END
GO

-- [Fix 6] At most two active, unexpired keys per application, so keys can be rotated without downtime.
CREATE OR ALTER PROCEDURE broker.usp_ApiKey_Create
    @KeyId     uniqueidentifier,
    @AppId     uniqueidentifier,
    @Prefix    varchar(32),
    @Hash      varbinary(32),
    @ExpiresAt datetime2(3) = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    BEGIN TRAN;

    IF NOT EXISTS (SELECT 1 FROM broker.Applications WITH (UPDLOCK, HOLDLOCK) WHERE AppId = @AppId)
    BEGIN
        ROLLBACK;
        THROW 50404, N'Application was not found.', 1;
    END

    IF (SELECT COUNT(*) FROM broker.ApiKeys
        WHERE AppId = @AppId AND IsActive = 1 AND (ExpiresAt IS NULL OR ExpiresAt > SYSUTCDATETIME())) >= 2
    BEGIN
        ROLLBACK;
        THROW 50409, N'The application already has two active keys. Deactivate one before issuing another.', 1;
    END

    INSERT broker.ApiKeys (KeyId, AppId, Prefix, Hash, ExpiresAt) VALUES (@KeyId, @AppId, @Prefix, @Hash, @ExpiresAt);

    COMMIT;

    SELECT KeyId, AppId, Prefix, IsActive, CreatedAt, ExpiresAt FROM broker.ApiKeys WHERE KeyId = @KeyId;
END
GO

CREATE OR ALTER PROCEDURE broker.usp_ApiKey_Deactivate
    @AppId uniqueidentifier,
    @KeyId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    UPDATE broker.ApiKeys SET IsActive = 0 WHERE KeyId = @KeyId AND AppId = @AppId;
    IF @@ROWCOUNT = 0
        THROW 50404, N'API key was not found.', 1;
END
GO

CREATE OR ALTER PROCEDURE broker.usp_ApiKey_List
    @AppId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT KeyId, AppId, Prefix, IsActive, CreatedAt, ExpiresAt
    FROM broker.ApiKeys WHERE AppId = @AppId ORDER BY CreatedAt;
END
GO

-- Authentication lookup. Only keys that are active, unexpired and belong to an active application resolve.
CREATE OR ALTER PROCEDURE broker.usp_ApiKey_GetByPrefix
    @Prefix varchar(32)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT k.KeyId, k.AppId, k.Hash, a.Name AS AppName, a.IsAdmin
    FROM broker.ApiKeys k
    JOIN broker.Applications a ON a.AppId = k.AppId
    WHERE k.Prefix = @Prefix
      AND k.IsActive = 1
      AND a.IsActive = 1
      AND (k.ExpiresAt IS NULL OR k.ExpiresAt > SYSUTCDATETIME());
END
GO

-- Creates the first admin application and key from configuration. Safe to run on every start.
CREATE OR ALTER PROCEDURE broker.usp_Application_EnsureBootstrapAdmin
    @AppId  uniqueidentifier,
    @Name   nvarchar(100),
    @KeyId  uniqueidentifier,
    @Prefix varchar(32),
    @Hash   varbinary(32)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    BEGIN TRAN;

    IF NOT EXISTS (SELECT 1 FROM broker.ApiKeys WITH (UPDLOCK, HOLDLOCK) WHERE Prefix = @Prefix)
    BEGIN
        DECLARE @ExistingAppId uniqueidentifier = (SELECT AppId FROM broker.Applications WHERE Name = @Name);
        IF @ExistingAppId IS NULL
            INSERT broker.Applications (AppId, Name, IsAdmin) VALUES (@AppId, @Name, 1);
        ELSE
            SET @AppId = @ExistingAppId;

        INSERT broker.ApiKeys (KeyId, AppId, Prefix, Hash) VALUES (@KeyId, @AppId, @Prefix, @Hash);
    END

    COMMIT;
END
GO

CREATE OR ALTER PROCEDURE broker.usp_Permission_Grant
    @AppId        uniqueidentifier,
    @ResourceType varchar(20),
    @ResourceId   uniqueidentifier,
    @Permission   varchar(20)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM broker.Applications WHERE AppId = @AppId)
        THROW 50404, N'Application was not found.', 1;

    IF (@ResourceType = 'Topic' AND NOT EXISTS (SELECT 1 FROM broker.Topics WHERE TopicId = @ResourceId AND IsDeleted = 0))
       OR (@ResourceType = 'Subscription' AND NOT EXISTS (SELECT 1 FROM broker.Subscriptions WHERE SubscriptionId = @ResourceId AND Status <> 'Deleted'))
        THROW 50404, N'The resource was not found.', 1;

    IF NOT EXISTS (SELECT 1 FROM broker.AppPermissions
                   WHERE AppId = @AppId AND ResourceType = @ResourceType AND ResourceId = @ResourceId AND Permission = @Permission)
        INSERT broker.AppPermissions (AppId, ResourceType, ResourceId, Permission)
        VALUES (@AppId, @ResourceType, @ResourceId, @Permission);
END
GO

CREATE OR ALTER PROCEDURE broker.usp_Permission_Revoke
    @AppId        uniqueidentifier,
    @ResourceType varchar(20),
    @ResourceId   uniqueidentifier,
    @Permission   varchar(20)
AS
BEGIN
    SET NOCOUNT ON;
    DELETE broker.AppPermissions
    WHERE AppId = @AppId AND ResourceType = @ResourceType AND ResourceId = @ResourceId AND Permission = @Permission;
END
GO

CREATE OR ALTER PROCEDURE broker.usp_Permission_List
    @AppId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT AppId, ResourceType, ResourceId, Permission
    FROM broker.AppPermissions WHERE AppId = @AppId
    ORDER BY ResourceType, ResourceId, Permission;
END
GO

CREATE OR ALTER PROCEDURE broker.usp_Permission_Check
    @AppId        uniqueidentifier,
    @ResourceType varchar(20),
    @ResourceId   uniqueidentifier,
    @Permission   varchar(20)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Allowed FROM broker.fn_HasPermission(@AppId, @ResourceType, @ResourceId, @Permission);
END
GO

-- [Fix 9] Webhook host allowlist.
CREATE OR ALTER PROCEDURE broker.usp_AllowedHost_Add
    @Host    nvarchar(255),
    @AddedBy uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM broker.WebhookAllowedHosts WHERE Host = @Host)
        INSERT broker.WebhookAllowedHosts (Host, AddedBy) VALUES (@Host, @AddedBy);
END
GO

CREATE OR ALTER PROCEDURE broker.usp_AllowedHost_Remove
    @Host nvarchar(255)
AS
BEGIN
    SET NOCOUNT ON;
    DELETE broker.WebhookAllowedHosts WHERE Host = @Host;
    IF @@ROWCOUNT = 0
        THROW 50404, N'Host is not on the allowlist.', 1;
END
GO

CREATE OR ALTER PROCEDURE broker.usp_AllowedHost_List
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Host, AddedBy, AddedAt FROM broker.WebhookAllowedHosts ORDER BY Host;
END
GO

-- Adds configured hosts that are missing. Never removes hosts an Admin added.
CREATE OR ALTER PROCEDURE broker.usp_AllowedHost_Seed
    @Hosts broker.HostList READONLY
AS
BEGIN
    SET NOCOUNT ON;
    INSERT broker.WebhookAllowedHosts (Host)
    SELECT h.Host FROM @Hosts h
    WHERE NOT EXISTS (SELECT 1 FROM broker.WebhookAllowedHosts w WHERE w.Host = h.Host);
END
GO
