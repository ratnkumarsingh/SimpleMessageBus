# Departures from the Phase 1 specification

The specification is `Internal_Message_Broker_Phase1_Architecture.pdf` (v1.0). This file lists every place where the build differs from it, and why. Fixes for gaps in the spec are marked **[Fix n]**, matching the implementation plan.

## Data access and tooling

| Spec | Build | Reason |
|---|---|---|
| EF Core for CRUD (sections 2 and 5) | No Entity Framework. All data logic lives in SQL Server in the `broker` schema: tables, stored procedures, views, functions and one table type. .NET calls procedures through plain ADO.NET (`Microsoft.Data.SqlClient`): an in-repo `SqlHelper` in the shape of Microsoft's Data Access Application Block, explicit parameters, and hand-written mappers from rows to records. No ORM or micro-ORM. | Project requirement. |
| Migrations not specified | DbUp. `db/Migrations` scripts run once and are journaled in `broker.SchemaVersions`. `db/Programmability` scripts (`CREATE OR ALTER`) run on every deploy. `Broker:Database:DeploySchemaOnStartup` turns this off where DBAs apply the scripts. | Reviewable SQL with no generated code. |
| — | `db/build-release-script.ps1` writes one `sqlcmd` script from the same embedded scripts, in the same order. It brings a database at any version up to date. Each migration runs in its own transaction, only when it has no row in `broker.SchemaVersions`, and then writes that row, so DbUp and the script can be mixed. | DBAs review and apply one file per release. |
| WireMock.Net for webhook tests (plan) | Tests use `FakeWebhookEndpoint`, a small Kestrel server in the test project that records calls and answers with a configured status, body, headers and delay. | WireMock.Net 2.x needs `Microsoft.OpenApi` 3.x, but ASP.NET Core 10's OpenAPI package needs 2.x. |
| .NET 10 | .NET 10 by default; the whole solution also builds for .NET 9 with `-p:BrokerTargetFramework=net9.0`. The few APIs that differ (the OpenAPI document model, Blazor's not-found page, reconnect modal and resource preloader, `UseStatusCodePagesWithReExecute`, and `WebApplicationFactory.UseKestrel` in the tests) sit behind `#if NET10_0_OR_GREATER`. | Hosts that only have the .NET 9 runtime. .NET 9 support ends on 10 November 2026. |
| Integration tests on Testcontainers (section 16) | Tests run against a local SQL Server instance. The `BROKER_TEST_SQL` environment variable sets the server (default `localhost`, Windows auth). Each run creates a throwaway `BrokerDb_Test_*` database and drops it afterwards. | Project requirement: no Docker. |
| — | Columns that hold names and hosts use the `Latin1_General_100_CI_AS` collation, and so does the `broker.HostList` table type. | Names compare the same way whatever the server's default collation is. |

## Security

| Spec | Build | Reason |
|---|---|---|
| Key example `pk_live_7f3a...` | Keys look like `mbk_<12 chars>_<43 chars base64url>`. The first 16 characters are the lookup prefix. Only the SHA-256 hash of the whole key is stored, and it is compared in constant time. | Fixed-length prefix gives an indexed lookup. |
| API keys stored on `Applications` | **[Fix 6]** Keys live in `broker.ApiKeys`. An application can have at most two active keys, so keys can be rotated without downtime. Deactivating a key, deactivating the application, or the key's expiry rejects the key immediately. | Rotation (section 12). |
| First Admin not specified | `Broker:Bootstrap:AdminApiKey` creates the first admin application and key on startup. This is safe to run on every start. | A new environment can then be set up through the API. |
| Allowlist kept in configuration only | **[Fix 9]** `broker.WebhookAllowedHosts`, managed with `/api/v1/admin/webhook-hosts`. `Broker:Webhooks:AllowedHosts` only adds hosts on startup and never removes one an Admin added. A host is a bare DNS name or IP address, compared case-insensitively. Changing a subscription's settings re-checks only a new URL. | Spec: "allowlist maintained by Admins". |
| Secret rotation drops the old secret | **[Fix 7]** The previous secret stays valid for 24 hours, and webhook calls carry both signatures during that time. | Receivers can switch over without failed deliveries. |
| Webhook secrets encrypted with Data Protection | `Broker:DataProtection:KeysDirectory` stores the key ring (DPAPI-protected on Windows). It is required in production: without it, a service account with no profile keeps keys in memory, and after a restart stored secrets cannot be decrypted. | Secrets must survive restarts. |
| — | `Broker:Webhooks:RequireHttps` (default true) can be turned off for local development against plain-HTTP receivers. | Local testing. |

## API

| Spec | Build | Reason |
|---|---|---|
| Admin endpoints for applications and permissions implied (section 12) | Admin endpoints under `/api/v1/admin`:<br>• `applications`: POST, GET, GET `{id}`, PATCH `{id}` to activate or deactivate.<br>• `applications/{id}/keys`: POST, GET, DELETE `{keyId}`.<br>• `applications/{id}/permissions`: GET, POST, POST `revoke`.<br>• `webhook-hosts`: GET, POST, DELETE `{host}`. | The spec names these operations but not their routes. |
| Error bodies are RFC 9457 | Every error, including 401 challenges and model-binding 400s, uses a `urn:message-broker:problem:*` type (see `MessageBroker.Contracts.ProblemTypes`). | Clients match on one set of types. |
| `correlationId` in the envelope | Optional. When omitted, it defaults to the new message ID. | Every message stays traceable. |
| Subscription PATCH | The delivery mode cannot change. `status` accepts only `Active` or `Paused`; DELETE removes the subscription. `clearTtl: true` removes the subscription TTL. | Keeps PATCH unambiguous. |
| Plain secrets and keys | They are returned once, on create or rotate, and never in reads or lists. | Section 12. |
| Pull receive limits | `maxMessages` defaults to 1 and `waitSeconds` defaults to 0 (return at once). Out-of-range values are clamped to 1–32 and 0–30 rather than rejected. A long poll wakes on a publish or requeue in this process, and also re-checks every second so it sees retries whose backoff has ended. | Lenient clients; [Fix 11] covers the in-process signal. |
| `attempt` on a received delivery | It is the attempt within the current retry budget (1 to maxAttempts) and starts again at 1 after a DLQ requeue. The attempt history (`GET /messages/{id}`) numbers every attempt and keeps counting. | **[Fix 1]** |
| DLQ listing | `GET /subscriptions/{id}/deadletters?pageSize=&before=&includeRequeued=` returns `{ items, nextBefore }`, newest first, using keyset paging on `DeadLetterId`. `pageSize` defaults to 50 (maximum 200). Requeued entries are hidden unless `includeRequeued=true`. | Stable paging while new entries arrive. |
| Requeue | Resets the retry budget and clears the delivery's expiry, so an `Expired` entry is delivered when requeued. | An operator's requeue is a decision to deliver. |
| Requeue of something not in the DLQ | 409 Conflict. A delivery whose subscription was deleted also returns 409. | Spec lists no code for these cases. |

## Hosting

| Spec | Build | Reason |
|---|---|---|
| Single host runs API and dispatcher | `Broker:Dispatcher:Enabled` (default true) controls whether this host runs the background loops: maintenance every `MaintenanceIntervalSeconds` and retention every `Broker:Retention:IntervalMinutes` (60), which deletes in batches of `BatchSize` (5000). | Tests run single passes on demand. A later scale-out can split API and dispatcher. |
| — | Operator commands on the host executable: `new-api-key` prints a key for `Broker:Bootstrap:AdminApiKey`; `release-script <path>` writes the DBA script. Both exit without starting the server. | No separate tool to ship. |
| `/health/ready` | Reports two checks, `sql` and `dispatcher`, as JSON. Each SQL call has a 2-second budget, and the heartbeat age is measured on the database clock. | Shows which dependency failed. |
| Recovery after restart: "lapsed leases redeliver within one lock duration" (section 15) | A lease held when the broker stopped lapses at most one lock duration after it was taken. The next maintenance pass, every `MaintenanceIntervalSeconds`, records it as a `LeaseExpired` attempt, and the delivery is retried after that attempt's backoff. R01 checks redelivery within lock duration + maintenance interval + retry delay. | Section 16 criterion 6 requires lease expiry to be retried with the configured backoff, so redelivery cannot also be bounded by the lock duration alone. With the defaults (60 s lock, 5 s maintenance, 30 s first retry ± 20% jitter) it is at most about 101 s. |
| Load test with NBomber or k6 (section 16) | NBomber 6.6 (`tests/MessageBroker.LoadTests`, L01). It also measures publish-to-receive p95 through a pull consumer, the section 15 "under 1 second" target. | NBomber is free for personal use only; an organization needs a licence, or can port the scenario to k6. |

## Webhook delivery

| Spec | Build | Reason |
|---|---|---|
| 202 keeps the lease open | A 202 frees the delivery's slot under `MaxConcurrentDeliveries` at once. The lease stays open until a REST ACK or NACK, or until it expires, after which maintenance retries it. | The concurrency limit caps calls in flight, not deliveries waiting for a subscriber to settle them. |
| Failed-attempt details | `ErrorCode` is `Http<status>`, `Timeout`, `ConnectionError`, or `SigningFailed` (the secret could not be decrypted). `ErrorDetail` keeps the first 2000 characters of an error response body. The request payload is never recorded. | Gives support enough to diagnose a failure without storing payloads twice. |
| Circuit breaker | Any non-2xx status, timeout or connection error counts as a failure. A 202 counts as a success. `SigningFailed` does not affect the circuit. The circuit state is in memory **[Fix 11]**. | Only the endpoint's own behaviour should open its circuit. |
| Host allowlist | Checked when a subscription is created and when its URL changes, not on every call. Removing a host does not stop subscriptions that already use it; pause or delete them. | Avoids a database read per call. |


## SignalR delivery

| Spec | Build | Reason |
|---|---|---|
| Hub at `/hubs/deliveries` with Subscribe, Ack, Nack | Also `Unsubscribe` (stops new deliveries to that connection) and `Renew` (returns the new `LockedUntil`). The hub authenticates with the API key, sent as the `access_token` query parameter, which is redacted from logs. | Long-running handlers need renewals; clients need a way to leave one subscription and keep the connection. |
| Subscribe permission | Joining needs Receive on the subscription (Admins may join any). Joining an unknown subscription, or one that is not in SignalR mode, fails. | Section 12 permissions apply to every channel. |
| Hub errors | A failed hub call raises `HubException` with the message `<Kind>: <detail>`, where Kind is `Forbidden`, `NotFound`, `Conflict`, `LeaseLost` or `Validation`. | SignalR has no status codes; clients match on the kind. |
| Delivery to one client | Each delivery goes to exactly one connection of the subscription, round-robin in join order, never to a group. With no connection, nothing is leased. | Spec section 8.3. |
| `MaxConcurrentDeliveries` | Counts deliveries sent over SignalR and not yet settled (ACK, NACK, or lease end). A renewal keeps the delivery counted. | A client cannot be flooded with more unsettled work than the limit. |
| Connection registry | In memory **[Fix 11]**. After a broker restart, clients reconnect and Subscribe again; the Contracts `SignalRDeliveryListener` does this automatically, retrying forever (0, 2, 5, 10, then every 30 s). Deliveries held by a client that disconnects are retried after their lease expires. | Single-instance state, to be replaced with a backplane in Phase 3. |

## Additions: admin dashboard

Not in the Phase 1 specification; added on request to watch the broker.

| Addition | Build | Reason |
|---|---|---|
| Dashboard read endpoints (Admin) | `GET /api/v1/admin/overview` (totals, per-minute throughput over 5–1440 minutes, subscription health with circuit state and connected clients), `GET /api/v1/admin/messages` (filtered keyset search) and `GET /api/v1/admin/deadletters` (DLQ across every subscription, without payloads). Backed by `usp_Admin_GetOverview`, `usp_Admin_Message_Search` and `usp_Admin_DeadLetter_Search`, with indexes in migration `0003_DashboardIndexes`. | The traceability API answers "what happened to this message"; operators also need "what is happening now" without SQL access. |
| Admin hub `/hubs/admin` | Admin keys only (403 at negotiate otherwise). Sends `Activity` batches (`Published`, `DeliveryChanged`) every 250 ms and `OverviewChanged` at most once a second; nothing is queued while no dashboard is connected. In memory **[Fix 11]**. | Live updates without every dashboard polling the database. |
| Settle procedures return the message ID | `usp_Delivery_Ack` and `usp_DeadLetter_Requeue` return `MessageId`; `usp_Delivery_Nack` adds it to its result. | Lets the activity feed name the message a settlement belongs to. |
| `MessageBroker.Dashboard` | A separate Blazor Server host; read-only except DLQ requeue; sign-in with an Admin key kept in an encrypted cookie. | Keeps UI out of the broker process and gives operators a view without SQL access. |
