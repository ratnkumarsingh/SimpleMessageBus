-- Publish and fan-out (spec section 8.1). The message and one delivery per Active or Paused
-- subscription are written in one transaction; the caller returns 201 only after this commits.
--
-- [Fix 2] Idempotency: a quick lookup handles the common retry. Two concurrent publishes with the
-- same key both miss the lookup; the unique filtered index rejects the second insert (2601/2627),
-- and the CATCH block returns the winner's MessageId instead of failing.
CREATE OR ALTER PROCEDURE broker.usp_Message_Publish
    @MessageId      uniqueidentifier,
    @TopicName      nvarchar(100),
    @AppId          uniqueidentifier,
    @MessageType    nvarchar(200),
    @CorrelationId  nvarchar(100),
    @IdempotencyKey nvarchar(100) = NULL,
    @Payload        nvarchar(max),
    @Properties     nvarchar(max) = NULL,
    @TtlSeconds     int = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    DECLARE @TopicId uniqueidentifier, @DefaultTtl int;
    SELECT @TopicId = TopicId, @DefaultTtl = DefaultTtlSeconds
    FROM broker.Topics WHERE Name = @TopicName AND IsDeleted = 0;

    IF @TopicId IS NULL
        THROW 50404, N'Topic was not found.', 1;
    IF NOT EXISTS (SELECT 1 FROM broker.fn_HasPermission(@AppId, 'Topic', @TopicId, 'Publish') WHERE Allowed = 1)
        THROW 50403, N'The application is not permitted to publish to this topic.', 1;

    DECLARE @ExistingId uniqueidentifier;
    IF @IdempotencyKey IS NOT NULL
    BEGIN
        SELECT @ExistingId = MessageId FROM broker.Messages
        WHERE TopicId = @TopicId AND PublisherAppId = @AppId AND IdempotencyKey = @IdempotencyKey;

        IF @ExistingId IS NOT NULL
        BEGIN
            SELECT @ExistingId AS MessageId,
                   (SELECT COUNT(*) FROM broker.Deliveries WHERE MessageId = @ExistingId) AS DeliveryCount,
                   CAST(1 AS bit) AS IsDuplicate;
            RETURN;
        END
    END

    DECLARE @Now datetime2(3) = SYSUTCDATETIME();
    DECLARE @Ttl int = COALESCE(@TtlSeconds, @DefaultTtl);
    DECLARE @ExpiresAt datetime2(3) = CASE WHEN @Ttl IS NULL THEN NULL ELSE DATEADD(second, @Ttl, @Now) END;
    DECLARE @DeliveryCount int;

    BEGIN TRY
        BEGIN TRAN;

        INSERT broker.Messages
            (MessageId, TopicId, MessageType, CorrelationId, PublisherAppId, IdempotencyKey, Payload, Properties, CreatedAt, ExpiresAt)
        VALUES
            (@MessageId, @TopicId, @MessageType, @CorrelationId, @AppId, @IdempotencyKey, @Payload, @Properties, @Now, @ExpiresAt);

        -- A delivery expires at the earlier of the message expiry and the subscription TTL.
        INSERT broker.Deliveries (MessageId, SubscriptionId, Status, AvailableAt, ExpiresAt, CreatedAt)
        SELECT @MessageId, s.SubscriptionId, 0, @Now,
               CASE
                   WHEN s.TtlSeconds IS NULL THEN @ExpiresAt
                   WHEN @ExpiresAt IS NULL OR DATEADD(second, s.TtlSeconds, @Now) < @ExpiresAt THEN DATEADD(second, s.TtlSeconds, @Now)
                   ELSE @ExpiresAt
               END,
               @Now
        FROM broker.Subscriptions s
        WHERE s.TopicId = @TopicId AND s.Status IN ('Active', 'Paused');

        SET @DeliveryCount = @@ROWCOUNT;

        COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;

        IF ERROR_NUMBER() IN (2601, 2627) AND @IdempotencyKey IS NOT NULL
        BEGIN
            SELECT @ExistingId = MessageId FROM broker.Messages
            WHERE TopicId = @TopicId AND PublisherAppId = @AppId AND IdempotencyKey = @IdempotencyKey;

            IF @ExistingId IS NOT NULL
            BEGIN
                SELECT @ExistingId AS MessageId,
                       (SELECT COUNT(*) FROM broker.Deliveries WHERE MessageId = @ExistingId) AS DeliveryCount,
                       CAST(1 AS bit) AS IsDuplicate;
                RETURN;
            END
        END;

        THROW;
    END CATCH

    SELECT @MessageId AS MessageId, @DeliveryCount AS DeliveryCount, CAST(0 AS bit) AS IsDuplicate;
END
GO
