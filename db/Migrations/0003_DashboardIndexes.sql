-- Indexes for the admin dashboard's queries (Dashboard.sql): topic-filtered message paging and the
-- per-minute throughput series. Deliveries by MessageId already seek UQ_Deliveries_MessageSubscription.

CREATE INDEX IX_Messages_Topic ON broker.Messages (TopicId, MessageSeq DESC);
CREATE INDEX IX_DeadLetters_DeadLetteredAt ON broker.DeadLetters (DeadLetteredAt) INCLUDE (SubscriptionId, Reason, RequeuedAt);
CREATE INDEX IX_DeliveryAttempts_EndedAt ON broker.DeliveryAttempts (EndedAt) INCLUDE (Outcome) WHERE EndedAt IS NOT NULL;
GO
