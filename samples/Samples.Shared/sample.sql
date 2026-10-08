-- Schema for the sample applications. A real publisher and subscriber would each own a database;
-- the samples share one, keyed by subscriber name. Safe to run on every start.
IF SCHEMA_ID(N'sample') IS NULL EXEC(N'CREATE SCHEMA sample');
GO

-- Publisher side: the business table and the outbox, written in one transaction.
IF OBJECT_ID(N'sample.Payments') IS NULL
CREATE TABLE sample.Payments
(
    PaymentId nvarchar(50)  NOT NULL CONSTRAINT PK_Payments PRIMARY KEY,
    Amount    decimal(18,2) NOT NULL,
    Currency  char(3)       NOT NULL,
    CreatedAt datetime2(3)  NOT NULL CONSTRAINT DF_Payments_CreatedAt DEFAULT SYSUTCDATETIME()
);
GO

IF OBJECT_ID(N'sample.Outbox') IS NULL
BEGIN
    CREATE TABLE sample.Outbox
    (
        OutboxId      bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Outbox PRIMARY KEY,
        TopicName     nvarchar(100)  NOT NULL,
        MessageType   nvarchar(200)  NOT NULL,
        CorrelationId nvarchar(100)  NOT NULL,
        Payload       nvarchar(max)  NOT NULL CONSTRAINT CK_Outbox_Payload CHECK (ISJSON(Payload) = 1),
        CreatedAt     datetime2(3)   NOT NULL CONSTRAINT DF_Outbox_CreatedAt DEFAULT SYSUTCDATETIME(),
        Attempts      int            NOT NULL CONSTRAINT DF_Outbox_Attempts DEFAULT 0,
        LastError     nvarchar(1000) NULL,
        SentAt        datetime2(3)   NULL,
        MessageId     uniqueidentifier NULL,
        -- The broker refused the row (4xx other than 408/429); it will never be sent as it is.
        RejectedAt    datetime2(3)   NULL
    );
    CREATE INDEX IX_Outbox_Pending ON sample.Outbox (OutboxId) WHERE SentAt IS NULL AND RejectedAt IS NULL;
END
GO

-- Subscriber side: the business table and the deduplication table (spec section 10, "Idempotency").
IF OBJECT_ID(N'sample.Invoices') IS NULL
CREATE TABLE sample.Invoices
(
    Subscriber   nvarchar(50)  NOT NULL,
    PaymentId    nvarchar(50)  NOT NULL,
    Amount       decimal(18,2) NOT NULL,
    Status       nvarchar(20)  NOT NULL,
    -- How often the business change ran; deduplication keeps it at 1.
    TimesApplied int           NOT NULL,
    PaidAt       datetime2(3)  NOT NULL,
    CONSTRAINT PK_Invoices PRIMARY KEY (Subscriber, PaymentId)
);
GO

IF OBJECT_ID(N'sample.ProcessedMessages') IS NULL
CREATE TABLE sample.ProcessedMessages
(
    Subscriber  nvarchar(50)     NOT NULL,
    MessageId   uniqueidentifier NOT NULL,
    ProcessedAt datetime2(3)     NOT NULL,
    CONSTRAINT PK_ProcessedMessages PRIMARY KEY (Subscriber, MessageId)
);
GO

/* Records a payment and its PaymentProcessed.v1 event in one transaction. */
CREATE OR ALTER PROCEDURE sample.usp_Payment_Record
    @PaymentId nvarchar(50),
    @Amount    decimal(18,2),
    @Currency  char(3),
    @TopicName nvarchar(100)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRY
        BEGIN TRAN;
        INSERT sample.Payments (PaymentId, Amount, Currency) VALUES (@PaymentId, @Amount, @Currency);
        INSERT sample.Outbox (TopicName, MessageType, CorrelationId, Payload)
        VALUES (@TopicName, N'PaymentProcessed.v1', @PaymentId,
                (SELECT @PaymentId AS paymentId, @Amount AS amount, @Currency AS currency FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));
        SELECT CAST(SCOPE_IDENTITY() AS bigint) AS OutboxId;
        COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        THROW;
    END CATCH
END
GO

/* Unsent rows, oldest first. */
CREATE OR ALTER PROCEDURE sample.usp_Outbox_GetPending
    @BatchSize int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@BatchSize) OutboxId, TopicName, MessageType, CorrelationId, Payload, Attempts
    FROM sample.Outbox
    WHERE SentAt IS NULL AND RejectedAt IS NULL
    ORDER BY OutboxId;
END
GO

CREATE OR ALTER PROCEDURE sample.usp_Outbox_MarkSent
    @OutboxId  bigint,
    @MessageId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE sample.Outbox
    SET SentAt = SYSUTCDATETIME(), MessageId = @MessageId, Attempts = Attempts + 1, LastError = NULL
    WHERE OutboxId = @OutboxId;
END
GO

/* A failed send. @Reject = 1 stops retrying the row. */
CREATE OR ALTER PROCEDURE sample.usp_Outbox_MarkFailed
    @OutboxId bigint,
    @Error    nvarchar(1000),
    @Reject   bit
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE sample.Outbox
    SET Attempts = Attempts + 1, LastError = @Error,
        RejectedAt = CASE WHEN @Reject = 1 THEN SYSUTCDATETIME() END
    WHERE OutboxId = @OutboxId;
END
GO

/*
   Applies a payment to the subscriber's invoice exactly once per message. The ProcessedMessages
   insert and the business change commit together; a primary key violation means the message was
   processed before, so nothing changes and IsDuplicate = 1 (the caller still ACKs).
*/
CREATE OR ALTER PROCEDURE sample.usp_Invoice_ApplyPayment
    @Subscriber nvarchar(50),
    @MessageId  uniqueidentifier,
    @PaymentId  nvarchar(50),
    @Amount     decimal(18,2)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRY
        BEGIN TRAN;
        INSERT sample.ProcessedMessages (Subscriber, MessageId, ProcessedAt) VALUES (@Subscriber, @MessageId, SYSUTCDATETIME());

        UPDATE sample.Invoices
        SET Status = N'Paid', Amount = @Amount, TimesApplied = TimesApplied + 1, PaidAt = SYSUTCDATETIME()
        WHERE Subscriber = @Subscriber AND PaymentId = @PaymentId;
        IF @@ROWCOUNT = 0
            INSERT sample.Invoices (Subscriber, PaymentId, Amount, Status, TimesApplied, PaidAt)
            VALUES (@Subscriber, @PaymentId, @Amount, N'Paid', 1, SYSUTCDATETIME());
        COMMIT;
        SELECT CAST(0 AS bit) AS IsDuplicate;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        IF ERROR_NUMBER() IN (2601, 2627) AND ERROR_MESSAGE() LIKE N'%PK_ProcessedMessages%'
        BEGIN
            SELECT CAST(1 AS bit) AS IsDuplicate;
            RETURN;
        END;
        THROW;
    END CATCH
END
GO
