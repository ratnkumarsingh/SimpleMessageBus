# Internal Message Broker — operations runbook

This is the runbook for the Phase 1 broker: one host that runs the HTTP API, the SignalR hub and the dispatcher loops, backed by SQL Server. Where the build differs from the specification, `docs/spec-deviations.md` says how and why.

All examples use PowerShell and assume:

```powershell
$broker = 'https://broker.internal'          # base address of the broker host
$h = @{ Authorization = "ApiKey $adminKey" } # an Admin application's key
```

---

## 1. What runs where

| Part | Where | State |
|---|---|---|
| API (`/api/v1`), hubs (`/hubs/deliveries`, `/hubs/admin`), health (`/health/*`) | Broker host (`MessageBroker.Api`) | In memory: the admin activity feed (connected dashboards, queued events) |
| Dispatcher: lease loop, webhook and SignalR senders, maintenance (every 5 s), retention (hourly) | Same host, when `Broker:Dispatcher:Enabled` is true (default) | In memory: circuit breakers, SignalR connections, publish signal |
| Messages, deliveries, attempts, DLQ, applications, keys, permissions, allowlist, heartbeat | SQL Server, `broker` schema | Durable |
| Data Protection key ring (encrypts webhook secrets) | `Broker:DataProtection:KeysDirectory` | Durable — **back it up** |
| Admin dashboard (section 9) | Its own host (`MessageBroker.Dashboard`), talking to the broker over REST and `/hubs/admin` | Sign-in cookies, encrypted with its own key ring (`Dashboard:DataProtectionKeysDirectory`) |

Phase 1 supports **one broker instance**. The in-memory state above is not shared, so do not run two instances against one database.

---

## 2. Installation

### 2.1 Prerequisites
- SQL Server 2019 or later. The service account needs `db_owner` on the broker database when the broker deploys its own schema. With a DBA-applied script (2.2 B), `db_datareader`, `db_datawriter` and `EXECUTE` on schema `broker` are enough.
- .NET 10 runtime (ASP.NET Core) on the host.
- A TLS certificate for the host (Kestrel or a reverse proxy).

### 2.2 Database
Pick one way to deploy the schema.

**A. The broker deploys it (default).** With `Broker:Database:DeploySchemaOnStartup = true`, every start runs DbUp. New migrations from `db/Migrations` run once and are journaled in `broker.SchemaVersions`, and procedures, views and functions are re-applied. Set `Broker:Database:CreateDatabase = true` only for development.

**B. DBAs apply a reviewed script.** Generate one script from the build:

```powershell
./db/build-release-script.ps1 -OutFile artifacts/broker-release.sql
sqlcmd -b -S <server> -d <database> -E -i artifacts/broker-release.sql
```

Then set `Broker:Database:DeploySchemaOnStartup = false`. The script:
- brings a database at **any** earlier version up to date, so give DBAs the same file every release;
- runs each migration in its own transaction, only when `broker.SchemaVersions` has no row for it, and writes that row under the name DbUp uses (so switching between A and B later is safe);
- sets `QUOTED_IDENTIFIER` and the other required SET options itself; `sqlcmd` defaults to `QUOTED_IDENTIFIER OFF`, which filtered indexes reject.

Always pass `-b`, so `sqlcmd` stops at the first error. A failed migration rolls back and leaves no journal row; fix the cause and run the same script again.

### 2.3 Configuration
Put settings in `appsettings.Production.json`, environment variables (`Broker__Webhooks__TimeoutSeconds=30`) or the service's secret store. Section 14 of the spec describes each setting; the ones operators must set are:

| Setting | Value |
|---|---|
| `ConnectionStrings:BrokerDb` | SQL connection string. Prefer integrated security. |
| `Broker:Bootstrap:AdminApiKey` | The first Admin key (2.4). |
| `Broker:DataProtection:KeysDirectory` | A directory only the service account can read, e.g. `D:\BrokerKeys`. **Required for production.** Without it, a service account with no user profile keeps the key ring in memory, and after a restart every webhook fails with `SigningFailed`. On Windows the keys are also encrypted with DPAPI for the service account. |
| `Broker:Webhooks:AllowedHosts` | Optional hosts to seed into the allowlist at startup. Startup only adds hosts; Admins remove them through the API. |
| `Broker:Webhooks:RequireHttps` | Leave `true`. `false` is for local development only. |
| `Logging:Console:FormatterName` | `json` (the default in `appsettings.json`). |

Defaults for new subscriptions live in `Broker:Defaults` (MaxAttempts 4, lock 60 s, retry 30 s → 900 s, 8 concurrent deliveries). Retention is in `Broker:Retention` (completed work 14 days, dead letters 90 days).

### 2.4 First Admin key
```powershell
dotnet MessageBroker.Api.dll new-api-key     # prints mbk_xxxxxxxxxxxx_<secret>
```
Store the key in the secret store and set it as `Broker:Bootstrap:AdminApiKey`. On every start the broker makes sure an Admin application (`Broker:Bootstrap:AdminName`) with that key exists. Once you have issued named Admin keys through the API (4.1), you can remove the setting. The bootstrap key keeps working until you deactivate it (5.1).

### 2.5 Running as a service
The host supports both service managers directly.

**Windows:**
```powershell
sc.exe create MessageBroker binPath= "C:\Broker\MessageBroker.Api.exe" start= delayed-auto obj= "NT SERVICE\MessageBroker"
sc.exe failure MessageBroker reset= 86400 actions= restart/5000/restart/5000/restart/30000
```
Set `ASPNETCORE_ENVIRONMENT=Production` and `ASPNETCORE_URLS` in the service's environment (registry `Environment` value), or configure Kestrel endpoints in `appsettings.Production.json`.

**Linux (systemd):** `Type=notify`, `ExecStart=/opt/broker/MessageBroker.Api`, `Restart=always`, `User=broker`.

### 2.6 Verify
```powershell
Invoke-RestMethod "$broker/health/live"     # {"status":"Healthy"}
Invoke-RestMethod "$broker/health/ready"    # sql and dispatcher both Healthy
```
`/health/ready` returns 503 while SQL Server takes more than 2 s to answer or the dispatcher heartbeat is more than 30 s old. Point the load balancer and monitoring at it.

---

## 3. Monitoring

### 3.1 Health
| Check | Unhealthy means | First action |
|---|---|---|
| `sql` | SQL Server unreachable or slower than 2 s | Check SQL Server, network, blocking (`sp_who2`), the connection string. |
| `dispatcher` | No heartbeat for 30 s | Check the host's log for `Maintenance pass failed`; check `Broker:Dispatcher:Enabled`; restart the service. |

### 3.2 Logs
Logs are JSON on stdout, one event per line, with scopes for `AppId`, `MessageId`, `CorrelationId`, `DeliveryId` and `SubscriptionId`. Payloads, API keys, webhook secrets and the hub's `access_token` are never written. Events worth alerting on:

| Event (message template) | Level | Meaning |
|---|---|---|
| `Circuit for subscription {SubscriptionId} opened after {Failures} consecutive failures` | Warning | A webhook endpoint is failing; deliveries to it pause 60 s at a time. |
| `Delivery {DeliveryId} dead-lettered after webhook failure {ErrorCode}` / `... after NACK from {AppId}` | Warning | A message reached the DLQ. Alert on the rate, not each event. |
| `Webhook request for delivery {DeliveryId} could not be signed` | Error | The webhook secret cannot be decrypted: the Data Protection key ring was lost or changed (7). |
| `Message store unavailable on {Method} {Path}` | Error | SQL failure while serving a request; clients got 503. |
| `Authentication failed for key {KeyPrefix} from {RemoteIp}` | Warning | A wrong, expired or deactivated key. Repeats from one IP suggest a misconfigured client or probing. |
| `Maintenance pass failed` / `Lease pass failed` / `Retention purge failed` | Warning | Background work is retrying; persistent repeats need attention. |

### 3.3 Useful SQL
```sql
-- Backlog per subscription
SELECT * FROM broker.vw_SubscriptionDeliveryCounts ORDER BY PendingCount DESC;

-- Recent dead letters with their last error
SELECT TOP 50 * FROM broker.vw_DeadLetterDetails ORDER BY DeadLetterId DESC;

-- Status of one message across its subscriptions
SELECT * FROM broker.vw_MessageStatus WHERE MessageId = @id;
```

---

## 4. Onboarding an application

`samples/SamplePublisher/SampleSetup.cs` runs these same steps in code (`SamplePublisher setup`).

### 4.1 Register it and issue a key
```powershell
$app = Invoke-RestMethod -Method Post "$broker/api/v1/admin/applications" -Headers $h `
       -ContentType application/json -Body '{"name":"billing-service"}'
$key = Invoke-RestMethod -Method Post "$broker/api/v1/admin/applications/$($app.appId)/keys" -Headers $h `
       -ContentType application/json -Body '{}'           # optional {"expiresAt":"2027-10-01T00:00:00Z"}
$key.apiKey   # shown once only; hand it over through the secret store
```
Add `"isAdmin": true` to the application body for an operator application.

### 4.2 Publisher
```powershell
$topic = Invoke-RestMethod -Method Post "$broker/api/v1/topics" -Headers $h -ContentType application/json `
         -Body '{"name":"payments","defaultTtlSeconds":86400}'
Invoke-RestMethod -Method Post "$broker/api/v1/admin/applications/$($app.appId)/permissions" -Headers $h `
  -ContentType application/json -Body (@{resourceType='Topic'; resourceId=$topic.topicId; permission='Publish'} | ConvertTo-Json)
```
Publishers should send an `Idempotency-Key` header and retry with the same key on 503 or a network error. A repeat returns 200 with the original message ID.

### 4.3 Subscriber
For a webhook, allow its host first (only once per host):
```powershell
Invoke-RestMethod -Method Post "$broker/api/v1/admin/webhook-hosts" -Headers $h -ContentType application/json `
  -Body '{"host":"billing.internal"}'
```
Create the subscription with the subscribing application as owner. The owner gets Receive on it automatically.
```powershell
$sub = Invoke-RestMethod -Method Post "$broker/api/v1/topics/$($topic.topicId)/subscriptions" -Headers $h `
  -ContentType application/json -Body (@{
     name = 'billing-invoices'; ownerAppId = $app.appId; deliveryMode = 'Webhook'   # or SignalR, Pull
     webhookUrl = 'https://billing.internal/webhooks/payments'
     maxAttempts = 5; lockDurationSeconds = 60; webhookTimeoutSeconds = 30
  } | ConvertTo-Json)
$sub.webhookSecret   # shown once only; the receiver verifies X-Broker-Signature with it
```
Validation rules: the webhook timeout must be shorter than the lock duration; the lock is at most 600 s; retry base ≤ retry max. Pull and SignalR subscriptions take no URL. Settings left out take `Broker:Defaults`.

### 4.4 Other grants
| Permission | Resource | Allows |
|---|---|---|
| `Publish` | Topic | Publishing to it. |
| `Receive` | Subscription | Pull receive, ACK/NACK/renew, joining it on the hub, reading its DLQ. |
| `Manage` | Subscription | As Receive. |

Revoke with `POST .../permissions/revoke` and the same body. Topic and subscription changes, DLQ requeue and message queries by correlation ID need an Admin key.

---

## 5. Routine operations

### 5.1 Rotate an application's API key
An application can hold two active keys, so rotation needs no downtime.
1. Issue the new key: `POST /api/v1/admin/applications/{appId}/keys`.
2. Deploy it to the application and confirm its requests succeed. Log events carry the `KeyPrefix` (first 16 characters).
3. Find the old key's `keyId` with `GET /api/v1/admin/applications/{appId}/keys` and deactivate it with `DELETE .../keys/{keyId}`. It stops working at once.

A third active key is refused with 409, so deactivate the old key before issuing another one. If a key leaks, deactivate it first; to cut off the whole application, send `PATCH /api/v1/admin/applications/{appId}` with `{"isActive": false}`.

### 5.2 Rotate a webhook secret
```powershell
$r = Invoke-RestMethod -Method Post "$broker/api/v1/subscriptions/$subId/webhook-secret" -Headers $h
```
For the next 24 hours, each webhook call carries two signatures, `sha256=<new>,sha256=<old>`. Give the receiver the new secret within that window; `WebhookSignature.Verify` in MessageBroker.Contracts accepts either signature.

### 5.3 Pause and resume a subscription
```powershell
Invoke-RestMethod -Method Patch "$broker/api/v1/subscriptions/$subId" -Headers $h -ContentType application/json -Body '{"status":"Paused"}'
Invoke-RestMethod -Method Patch "$broker/api/v1/subscriptions/$subId" -Headers $h -ContentType application/json -Body '{"status":"Active"}'
```
While a subscription is paused, messages still queue for it but nothing is leased. Deliveries that are already leased finish or time out. TTLs keep running, so a long pause can dead-letter messages as `Expired`.

### 5.4 Dead-letter queue
```powershell
# Newest first; pass nextBefore from the previous page as before=
$page = Invoke-RestMethod "$broker/api/v1/subscriptions/$subId/deadletters?pageSize=50" -Headers $h
$page.items | Format-Table deliveryId, reason, correlationId, attemptCount, lastError, lastFailureAt

# After fixing the cause, return one entry to the queue (Admin)
Invoke-RestMethod -Method Post "$broker/api/v1/deadletters/$deliveryId/requeue" -Headers $h
```
| Reason | Cause | Before requeueing |
|---|---|---|
| `MaxAttemptsExceeded` | Every retry failed | Fix the endpoint or the subscriber's bug. |
| `RejectedBySubscriber` | The subscriber NACKed with `deadLetter` | The message is invalid for that subscriber; requeue only when its code has changed. |
| `Expired` | TTL passed before delivery | Usually leave it. A requeue clears the delivery's expiry, so the stale message is delivered. |

A requeued delivery gets a fresh retry budget. Its attempt history keeps counting (`GET /api/v1/messages/{id}` shows every attempt). Requeueing something that is not in the DLQ returns 409. The API has no bulk requeue: loop over the page, and requeue slowly enough that the subscriber can keep up. The dashboard's **Dead letters** page (section 9) does that loop for up to 200 selected entries and reports each failure.

Across every subscription at once (Admin), with optional `topicId`, `subscriptionId`, `reason`, `from`, `to` (UTC) and `includeRequeued`:
```powershell
Invoke-RestMethod "$broker/api/v1/admin/deadletters?reason=MaxAttemptsExceeded&pageSize=100" -Headers $h
```
That listing leaves payloads out (`payload` is `null`); read the message for it.

### 5.5 Trace a message
```powershell
Invoke-RestMethod "$broker/api/v1/messages/$messageId" -Headers $h                   # deliveries + attempts
Invoke-RestMethod "$broker/api/v1/messages?correlationId=$corr" -Headers $h          # every message in the flow (Admin)
```
Message status is `InProgress`, `Completed`, `PartiallyDeadLettered` or `DeadLettered`. Deliveries cancelled because their subscription was deleted are ignored in that status.

To find messages without knowing their ID (Admin), search newest first with any of `topicId`, `status`, `messageType`, `correlationId`, `publisherAppId`, `from` and `to` (UTC); pass `nextCursor` back as `cursor` for the next page:
```powershell
Invoke-RestMethod "$broker/api/v1/admin/messages?status=DeadLettered&pageSize=50" -Headers $h
Invoke-RestMethod "$broker/api/v1/admin/overview?windowMinutes=60" -Headers $h      # totals, per-minute throughput, subscription health
```

### 5.6 Webhook allowlist
`GET`, `POST` and `DELETE /api/v1/admin/webhook-hosts[/{host}]`. Removing a host does not stop existing subscriptions that use it; pause or delete them as well.

### 5.7 Deleting topics and subscriptions
Deleting a subscription cancels its Pending and Leased deliveries; ACKs for them return 410. A topic can be deleted only once it has no subscriptions (otherwise 409).

---

## 6. Upgrades and restarts

1. With option B (2.2), have the DBAs apply the new release script before deploying.
2. Stop the service, replace the binaries, and start it. With option A, the schema deploys during startup, before requests are accepted.
3. Check `/health/ready`.

Effects of a restart:
- Requests in flight fail. Publishers retry with the same `Idempotency-Key`, so nothing is duplicated.
- Leases held at shutdown expire after their lock duration, and maintenance then retries them. Nothing is lost, but those deliveries can arrive twice, so subscribers must deduplicate on `messageId` (the samples' `ProcessedMessages` table shows how).
- Circuit breakers reset to Closed.
- SignalR clients reconnect and subscribe again. `SignalRDeliveryListener` does this itself, retrying at 0, 2, 5 and 10 s, then every 30 s.
- Long polls end; pull clients simply poll again.

Back up the broker database with your normal SQL Server plan, and back up the Data Protection keys directory with it. A database restored without its key ring cannot sign webhooks (7).

---

## 7. Troubleshooting

| Symptom | Likely cause | Action |
|---|---|---|
| Every webhook delivery fails with `SigningFailed` after a restart or move | The Data Protection key ring is missing or different (no `KeysDirectory`, or a new machine or account) | Restore the keys directory. If the keys are gone, rotate the secret of every webhook subscription (5.2) and send the new secrets to the receivers. |
| A subscription's backlog grows; its webhook calls stop | Circuit open (log: `Circuit ... opened`) | Fix the endpoint; the circuit tries one delivery every 60 s and closes on success. |
| SignalR subscription's backlog grows | No client connected (nothing is leased without one) | Check the subscriber is running and joined (log: `SignalR client ... joined subscription`). |
| Messages arrive twice | Lease expired before the ACK (slow handler, or lock too short) | Raise `lockDurationSeconds`, have the handler call renew, and deduplicate on `messageId`. |
| ACK returns 410 | The lease ended or the delivery was cancelled; the delivery is retried or was already settled | Treat as "do not retry the ACK". Shorten handler time or renew. |
| Creating a webhook subscription returns 400 | URL not HTTPS, host not allowlisted, or `webhookTimeoutSeconds` ≥ `lockDurationSeconds` | Read `errors` in the problem body. |
| 401 for a working key | Key deactivated or expired, application deactivated, or the header is not `Authorization: ApiKey <key>` | `GET /api/v1/admin/applications/{appId}/keys`. |
| 413 on publish | Payload over 256 KB | Send a reference (blob URL) instead of the data. |
| `/health/ready` 503, check `dispatcher` | Background loops stopped or SQL errors in maintenance | See 3.1. |
| Release script fails with `QUOTED_IDENTIFIER` | Script built by an old build, or edited | Regenerate it with `db/build-release-script.ps1`. |
| Dev only: a `crit` line about `Microsoft.WebTools.ApiEndpointDiscovery` at startup | Visual Studio sets `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES` for the machine | Harmless; it does not appear on servers. |

---

## 8. Local development and the samples

The `Development` settings use the local SQL Server instance (`BrokerDb`, created on first run), a fixed bootstrap key, and plain-HTTP webhooks to `localhost`.

```powershell
dotnet run --project src/MessageBroker.Api            # http://localhost:5080
dotnet run --project samples/SamplePublisher -- setup --AdminKey <Broker:Bootstrap:AdminApiKey from appsettings.Development.json>
dotnet run --project samples/WebhookSubscriber        # listens on http://localhost:5081
dotnet run --project samples/SignalRSubscriber
dotnet run --project samples/PullSubscriber
dotnet run --project samples/SamplePublisher -- --Generator:Count 14 --Generator:FailEvery 5 --Generator:InvalidEvery 7
```
**API explorers.** In Development, two browser UIs list every endpoint and let you call them: Swagger UI at http://localhost:5080/swagger (press **Authorize**) and Scalar at http://localhost:5080/scalar (fill in the ApiKey field under Authentication). Both take the whole header value, `ApiKey <key>`, for example with the bootstrap admin key. Other environments serve only the OpenAPI document at `/openapi/v1.json`, unless `Broker:ApiDocsUi` is `true`.

`setup` writes `samples/samples.local.json`, which holds API keys and a webhook secret: keep it out of source control. With these settings, each subscriber completes 10 payments and dead-letters 4. Two are `FAIL-` payments that fail 3 attempts (`MaxAttemptsExceeded`), and two are invalid payments the subscribers reject (`RejectedBySubscriber`). Use 5.4 and 5.5 to requeue one and trace it. A requeued `FAIL-` payment fails again and shows attempts 4–6 in its history.

**Admin dashboard.** `dotnet run --project src/MessageBroker.Dashboard` serves http://localhost:5090. Sign in with the bootstrap admin key; section 9 describes it.

**Blazor samples.** `setup` also onboards two Blazor Server apps on a `notifications` topic:
```powershell
dotnet run --project samples/BlazorSubscriber         # http://localhost:5083 — keep it open in a browser
dotnet run --project samples/BlazorPublisher          # http://localhost:5082 — fill in the form, press Send
```
Send publishes a `UserNotification` with a fresh `Idempotency-Key`. BlazorSubscriber receives it over its SignalR subscription and shows a toast in every open tab, coloured by level. A redelivered message is ACKed without a second toast, and a notification with no title is dead-lettered (`RejectedBySubscriber`). The subscriber keeps the last 50 notifications in memory only.

**Console samples.** `setup` also onboards them on the same `notifications` topic. To add them to an existing `samples.local.json` without replacing the other samples' keys, run `setup-console` instead:
```powershell
dotnet run --project samples/SamplePublisher -- setup-console --AdminKey <admin key>
dotnet run --project samples/ConsoleSubscriber        # webhook endpoint on http://localhost:5084
dotnet run --project samples/ConsolePublisher         # type: Deploy done | v1.2 is live | Success
dotnet run --project samples/ConsolePublisher -- --Count 5 --IntervalSeconds 1 --Level Warning
```
ConsoleSubscriber has three subscriptions, one per delivery mode, so each notification prints three times, once per channel, with the latency since publish:
```
12:03:04 [SignalR] SUCCESS Deploy done: v1.2 is live (from ConsolePublisher, 18 ms after publish)
12:03:04 [Webhook] SUCCESS Deploy done: v1.2 is live (from ConsolePublisher, 41 ms after publish)
12:03:04 [Pull] SUCCESS Deploy done: v1.2 is live (from ConsolePublisher, 63 ms after publish)
```
BlazorPublisher's notifications show up here too, and ConsolePublisher's appear as toasts in BlazorSubscriber. Each channel deduplicates and dead-letters on its own, like BlazorSubscriber. While ConsoleSubscriber is stopped, its webhook deliveries fail three attempts and are dead-lettered (`MaxAttemptsExceeded`); its Pull and SignalR deliveries wait until it starts again or their TTL runs out.

**Publishing from stored procedures and scheduled jobs.** Any stored procedure can publish by calling `sample.usp_Outbox_Enqueue` inside its own transaction; the event is stored only if the transaction commits (`sample.usp_Payment_Record` does this). The always-running SamplePublisher relay sends the rows, or a scheduler runs one pass and exits:
```powershell
sqlcmd -S . -E -d BrokerSamples -Q "EXEC sample.usp_Payment_Record 'PAY-SQL-1', 250, 'INR', 'payments'"
dotnet run --project samples/SamplePublisher -- relay-once      # relay-once: 1 sent, 0 rejected   (exit 0)
```
Exit codes: `0` everything sent, `1` some rows rejected by the broker (alert, do not retry), `2` stopped early with rows still pending: broker unreachable, or a key/topic problem shown in the warning (the next run sends them with the same `outbox-<id>` key). A job can also publish its own event with `samples/ActiveBatch/Publish-BrokerEvent.ps1` (same exit codes); `samples/ActiveBatch/README.md` describes both ActiveBatch job setups.

Tests: `dotnet test InternalMessageBroker.sln` (local SQL Server; `BROKER_TEST_SQL` overrides the server). It includes the concurrency (C01, C02) and restart (R01, R02) tests, which take about 25 s together.

The load test (L01) runs separately and should be built in Release:
```powershell
dotnet run -c Release --project tests/MessageBroker.LoadTests                              # 2-minute smoke at 100 msg/s, self-hosted broker
dotnet run -c Release --project tests/MessageBroker.LoadTests -- --DurationSeconds 1800    # the 30-minute report
dotnet run -c Release --project tests/MessageBroker.LoadTests -- --BrokerUrl https://broker.internal --AdminKey <admin key>
```
Self-hosted, it creates a throwaway `BrokerDb_Load_*` database (dropped afterwards) and runs the broker on a free local port. Against a deployed broker, it onboards its own `load-*` topic, applications and pull subscription; delete them afterwards (5.7). Other options are `--Rate` (default 100), `--P95Ms` (100), `--DeliveryP95Ms` (1000) and `--ReportFolder` (`artifacts/load-report`). The exit code is 1 when a threshold is missed. NBomber 6 is free for personal use only; an organization needs an NBomber licence. Results so far are in `docs/acceptance.md`.

---

## 9. Admin dashboard

`src/MessageBroker.Dashboard` is a Blazor Server app for watching the broker. It is read-only except for one action: requeueing dead letters. It is a separate host and reaches the broker only through the REST API and the admin hub (`/hubs/admin`), with the signed-in operator's own key, so it can run anywhere that reaches the broker.

| Page | Shows |
|---|---|
| Overview | Pending, leased and dead-lettered deliveries; published and completed in the chosen window (15 min to 24 h); a per-minute chart of published, completed, failed attempts and dead-lettered (with a table view); each subscription's backlog, webhook circuit state and connected SignalR clients; dispatcher heartbeat age |
| Messages | Every message, newest first, filtered by topic, status, type, correlation ID, publisher and time; the detail page shows the payload, properties, every delivery and its attempt history, and other messages with the same correlation ID |
| Dead letters | The DLQ across every subscription, with filters; requeue one entry or up to 200 selected (asks first, then reports each failure) |
| Topology | Topics with their subscriptions and settings (webhook host only, never the path or secret) and applications with their permissions |

**Live updates.** The dashboard holds one admin-hub connection per open browser tab. The broker sends a batch of message activity every 250 ms and an "overview changed" signal at most once a second, and pages re-read what they show (at most once a second). The badge at the bottom left shows the state: **Live**, **Reconnecting · polling** or **Offline · polling**. When it is not live, pages refresh every `FallbackPollSeconds` until the hub is back. The feed is per broker process [Fix 11], like the other in-memory state.

### 9.1 Deploy
Run it like the broker (2.5): `MessageBroker.Dashboard.exe` as a service, behind TLS. Settings (`Dashboard` section or `Dashboard__*` environment variables):

| Setting | Default | Notes |
|---|---|---|
| `Dashboard:BrokerUrl` | `http://localhost:5080/` | The broker's base address. |
| `Dashboard:DataProtectionKeysDirectory` | — | **Set it in production.** The key ring encrypts sign-in cookies. Without it, a restart can sign everyone out. Use a different directory from the broker's. |
| `Dashboard:SessionHours` | 8 | Sliding sign-in lifetime. |
| `Dashboard:FallbackPollSeconds` | 10 | Refresh interval while the live feed is down. |
| `Dashboard:RefreshThrottleMs` | 1000 | Pages re-read at most this often. |

### 9.2 Access
Operators sign in with an **Admin** API key: issue each operator a named Admin application and key (4.1, `"isAdmin": true`) rather than sharing the bootstrap key. The dashboard checks the key with the broker. It keeps the key only inside its sign-in cookie, which is encrypted, HttpOnly and SameSite=Strict, so the browser never sees the key in readable form. Every broker call is made with that operator's key, and the broker's log shows it as the caller. Deactivating the key (5.1) locks the operator out at their next request. **Sign out** clears the cookie. Put the dashboard on an internal network or behind your SSO proxy as well: anyone holding an Admin key can do more than the dashboard shows.

### 9.3 Troubleshooting
| Symptom | Check |
|---|---|
| "The broker did not accept that key" at sign-in | The key is wrong, expired or deactivated. |
| "That key is valid, but its application is not an Admin" | Use an Admin application's key. |
| Badge stays on **Reconnecting · polling** or **Offline · polling** | The dashboard cannot open `/hubs/admin`. Check `Dashboard:BrokerUrl`, and that any proxy between them passes WebSockets (or long polling). Pages still refresh on the fallback timer. |
| A subscription shows **Circuit open** | Its webhook endpoint is failing (section 7); waiting deliveries keep their attempts. |
| Everyone signed out after a restart | Set `Dashboard:DataProtectionKeysDirectory`. |
