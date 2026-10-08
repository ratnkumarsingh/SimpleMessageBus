-- Settlement reads a delivery's dead-letter history (the last requeue). Without this index that read
-- scans DeadLetters, and two concurrent settles of different deliveries can deadlock on each other's
-- rows. The settle procedures seek it with FORCESEEK, as they do UQ_DeliveryAttempts_Number.
CREATE INDEX IX_DeadLetters_Delivery ON broker.DeadLetters (DeliveryId) INCLUDE (RequeuedAt);
GO
