# Phase 1 acceptance

Section 16 of `Internal_Message_Broker_Phase1_Architecture.pdf` lists 16 acceptance criteria. Each one must be shown by automated tests or by the sample applications. This file maps each criterion to the tests that show it and records the last results.

Test IDs appear at the start of each test's display name, so `dotnet test --filter "DisplayName~P02"` runs one test. Letter prefixes:
- **U**: unit
- **D**: stored procedures against SQL Server
- **A**: REST API
- **K**: Contracts client
- **P**: push channels
- **W**: background loops
- **C**: concurrency
- **R**: restart
- **S**: samples
- **L**: load

## Criteria

| # | Criterion (spec section 16) | Shown by |
|---|---|---|
| 1 | A publisher publishes through the REST API and receives 201 only after the message and its deliveries are committed. | **A01**: 201 with `messageId` and `deliveryCount`. **D01**: the message and its deliveries are written in one transaction. **R01**: every message accepted while the broker was stopped and restarted is stored, and stored only once. |
| 2 | Republishing with the same Idempotency-Key creates no new message. | **D03**: the same key returns the original message. **D04** **[Fix 2]**: 20 concurrent publishes with one key create one message. **A01**: the repeat returns 200 with the same id. **S01**: the outbox publisher's retries after an outage create no duplicates. |
| 3 | Several subscriptions on one topic each receive an independent delivery. | **D01**: fan-out to every Active and Paused subscription. **P13**: a failing webhook subscription and a pull subscription on one topic progress independently. **C01**: a pull and a webhook subscription on one topic each complete all 5,000 messages. |
| 4 | The dispatcher delivers to a webhook subscriber and a SignalR subscriber; a 2xx response or a hub ACK completes the delivery. | **P01**: webhook 200 completes the delivery; the signature verifies. **P04**: 202 followed by a REST ACK. **P08**: SignalR Subscribe → Deliver → Ack. **S02**, **S04**: the webhook, SignalR and Blazor samples. |
| 5 | A pull subscriber ACKs with its lock token; a stale token returns 410. | **A05**: receive, ACK 204, ACK again 410. **D09**: a wrong token, an expired lease or a second ACK is `LeaseLost`. **C02**: an ACK that loses the race with lease expiry gets 410 and changes nothing. |
| 6 | NACK, failed webhook calls and lease expiry are recorded as attempts and retried with the configured backoff. | **D10** **[Fix 4]**: NACK backoff. **D14**: lease expiry. **P02**: webhook 500. **P03**: timeout. **P04**: 202 without an ACK. **P09**: a SignalR client that disconnects. **C01**: abandoned leases are recorded as `LeaseExpired` and retried. |
| 7 | A delivery that fails MaxAttempts times, or is NACKed with deadLetter, is in the DLQ with full error history. | **D11**: `MaxAttemptsExceeded`. **D12**: `RejectedBySubscriber`. **P02**: webhook attempts up to MaxAttempts. **P12**: SignalR NACK with deadLetter. |
| 8 | An Admin can requeue a DLQ entry and it is delivered again. | **D16** **[Fix 1]**: requeue resets the attempt budget, and the delivery can fail and dead-letter again. **A08**: requeue is Admin only. Also checked by hand with the samples (runbook 8). |
| 9 | A delivery past its TTL is dead-lettered with reason Expired, not delivered. | **D15**: Pending deliveries past `ExpiresAt` are dead-lettered as `Expired`. **D07**: lease skips expired deliveries. **W01**: the maintenance loop runs it. |
| 10 | The full history of a message is retrievable by MessageId, and all related messages by CorrelationId. | **D20**, **A09**: by id. **A09b**: by correlation id, Admin only. **K01**: the Contracts client's trace call. |
| 11 | A failing subscription never changes another subscription's deliveries. | **P13**. **P06**: an open circuit holds back only its own subscription. |
| 12 | The crash-and-restart, concurrency and push-channel tests pass. | **C01**, **C02**, **R01**, **R02**, **D28**, and **P01**–**P14** (with **P03b** and **P14a**). |
| 13 | Calls and hub connections without valid credentials, or outside granted permissions, are rejected. | **A02**–**A02d**. **P12**: Subscribe needs Receive. **D07b**, **D23c**. **A08**, **A09**: owner and publisher checks. |
| 14 | Health endpoints and structured logs with message and correlation IDs are in place. | **A10**, **A10b**: live and ready. **A11**: log scopes carry `MessageId`, `CorrelationId` and `AppId`; the token is redacted; payloads never appear. **W01**: heartbeat. |
| 15 | The sample publisher (with outbox) and the webhook, SignalR and pull sample subscribers (with deduplication) demonstrate the full success and failure paths. | **S01**: outbox. **S02**: each subscriber skips a redelivered message and still ACKs. **S03**: setup. A manual end-to-end check passed on 2026-10-08 (runbook 8): 10 completed and 4 dead-lettered per channel, then a requeue and a trace. |
| 16 | The load test meets the agreed targets in section 15. | **L01** (below). |

## Concurrency and restart tests

**C01.** One topic has a pull subscription and a webhook subscription, both with a 2-second lock. 5,000 messages give 10,000 deliveries. These run at the same time:
- 20 pull receivers;
- two independent lease loops (`LeaseLoop`, as two broker instances would run), with a channel that ACKs through the store;
- two maintenance loops.

Every consumer drops about 2% of its leases without settling them, so lease expiry races the normal path throughout. The test checks:
- every delivery is Completed, and its ACK succeeded exactly once;
- no `(DeliveryId, AttemptNumber)` was handed out twice;
- the attempt history matches what the consumers saw: one `Acked` attempt per delivery, one `LeaseExpired` attempt per dropped lease, and no open attempts;
- nothing is dead-lettered.

**C02.** 200 deliveries are leased with a 1-second lock. Each is settled (half ACK, half NACK) at a random moment between 0 and 1.6 s, while maintenance expires leases in a tight loop. Every delivery ends in exactly one state, matching whoever won:
- ACK won: `Completed` and `Acked`;
- NACK won: `Pending` and `Nacked`;
- expiry won: `Pending` and `LeaseExpired`, and the late settle got 410.

**R01.** A webhook endpoint answers in 150 ms, so calls are always in flight. A broker host runs with its background loops while one publisher sends about 50 messages per second. The host is stopped mid-load and a new one started on the same database 1 s later. The publisher keeps going through the outage and retries each message with its `Idempotency-Key`. The new host reads the secrets the old one protected, because both use the same Data Protection key directory. The test checks:
- every accepted message is stored once and completed;
- the leases the stopped host held are redelivered by the new host within lock duration + maintenance interval + retry delay (+2 s for polling and the call);
- nothing is dead-lettered.

**R02.** A `SignalRDeliveryListener` receives a delivery, the broker restarts, and the client reconnects and subscribes again by itself. It then receives and ACKs a delivery published to the new instance.

The restart tests stop the host in-process instead of killing an OS process. What matters is the state left behind, and it is the same: in-flight webhook calls are abandoned, their leases stay `Leased` in the database, SignalR connections drop and long polls end.

## L01: load

`dotnet run -c Release --project tests/MessageBroker.LoadTests` creates a throwaway database and hosts the broker on a real Kestrel port with its background loops running. NBomber then publishes over HTTP at 100 msg/s, after a 10-second warm-up. A pull consumer drains the subscription at the same time, so the broker is delivering while it accepts. The run fails (exit code 1) when:
- publish p95 is 100 ms or more;
- publish-to-receive p95 is 1 s or more;
- any publish fails;
- less than 95% of the target rate is achieved.

`--DurationSeconds 1800` gives the 30-minute report. `--BrokerUrl` with `--AdminKey` runs against a deployed broker. NBomber writes HTML and Markdown reports to `artifacts/load-report`.

| Run | Duration | Published | Failed | Rate | Publish p50 / p95 / p99 | Publish-to-receive p95 |
|---|---|---|---|---|---|---|
| 2-minute smoke, 2026-10-08 | 120 s | 12,000 | 0 | 100.0 msg/s | 3.1 / 16.8 / 18.3 ms | 46.2 ms (max 94.6 ms) |
| 30-minute report, 2026-10-08 | 1,800 s | 180,000 | 0 | 100.0 msg/s | 2.9 / 16.6 / 18.6 ms | 34.2 ms (max 259.9 ms) |
| 2-minute smoke with the admin activity feed, 2026-10-08 | 120 s | 12,000 | 0 | 100.0 msg/s | 3.1 / 18.1 / 24.5 ms | 40.9 ms (max 76.2 ms) |

Measured on one developer workstation (Windows 10, local SQL Server 2022 default instance), with the broker, SQL Server and NBomber on the same machine. The section 15 targets are starting values; repeat the 30-minute run on production-like hardware with measured volumes before sign-off.

NBomber 6 is free for personal use only, and an organization needs an NBomber licence to run it. k6 is the alternative the spec names.

## Last full run

On 2026-10-08, `dotnet test InternalMessageBroker.sln` passed all 251 tests (154 unit, 97 integration) in about 45 s, with no build warnings. D30, added later that day for a lease-loop timing fix, brings the suite to 252. Separate runs of the concurrency and restart tests that day reported:
- **C01**: 10,000 deliveries drained in 8.7 s. 10,209 attempts were handed out; 209 leases were dropped on purpose and recovered.
- **C02**: ACK won 58 races, NACK won 62 and lease expiry won 80.
- **R01**: 158 messages were accepted and 11 publish calls failed during the outage. At the stop, 8 leases were orphaned and 8 deliveries were pending; everything drained 2.4 s after publishing stopped.
- **R02**: the client rejoined 0.9 s after the restart.
