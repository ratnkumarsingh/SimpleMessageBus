# SimpleMessageBus — Internal Message Broker

A topic-based message broker for internal services, built on .NET 10 and SQL Server. Publishers send messages to a topic over HTTP; the broker stores them durably and delivers a copy to every subscription on that topic by **webhook**, **SignalR** or **pull**, with leases, retries, a circuit breaker and a dead-letter queue.

This is the Phase 1 build: a single broker host backed by one SQL Server database.

## Features

- **Durable publish and fan-out.** A publish writes the message and one delivery per subscription in one transaction. `Idempotency-Key` makes publisher retries safe.
- **Three delivery modes.**
  - *Webhook*: signed HTTPS `POST` (HMAC, with secret rotation); a 2xx response ACKs, 202 holds the lease for a later REST ACK.
  - *SignalR*: pushes each delivery to exactly one connected client, round-robin.
  - *Pull*: REST lease / ACK / NACK / renew, with long polling.
- **At-least-once delivery with leases.** An unsettled lease expires and the delivery is retried.
- **Retries and dead letters.** Exponential backoff with jitter (30 s → 900 s by default, 4 attempts), per-message and per-subscription TTL, and a DLQ with requeue.
- **Webhook circuit breaker.** After 5 consecutive failures a subscription pauses so waiting messages don't use up attempts.
- **Security.** API-key authentication, per-topic/subscription permissions, a webhook host allowlist, and webhook secrets encrypted with ASP.NET Core Data Protection.
- **Operations.** Health checks (`/health/live`, `/health/ready`), structured JSON logs, retention purge, and Swagger UI / Scalar API docs.
- **Admin dashboard.** A Blazor app that shows throughput, backlog, circuit state and every message's delivery history, updated live over SignalR, with dead-letter requeue.
- **Data access through stored procedures and views** with Dapper; no ORM. The schema is deployed by DbUp or as a single release script.

## Repository layout

| Path | Contents |
|---|---|
| `src/MessageBroker.Api` | The broker host: REST API (`/api/v1`), SignalR hub (`/hubs/deliveries`), health checks |
| `src/MessageBroker.Application` | Services, options and repository interfaces |
| `src/MessageBroker.Domain` | Enums, validation and the delivery state machine |
| `src/MessageBroker.Infrastructure` | Dapper repositories, schema deployer, Data Protection |
| `src/MessageBroker.Worker` | Dispatcher: lease loop, webhook and SignalR channels, maintenance and retention |
| `src/MessageBroker.Contracts` | Shared models and a client library (`BrokerClient`, `SignalRDeliveryListener`, webhook signature helpers) |
| `src/MessageBroker.Dashboard` | Admin dashboard (Blazor Server): overview, message browser, dead letters, topology |
| `db/` | Migrations, stored procedures, views, functions and `build-release-script.ps1` |
| `samples/` | Publisher and subscriber samples (see below) |
| `tests/` | Unit tests, integration tests against SQL Server, dashboard component tests (bUnit), and an NBomber load test |
| `docs/` | Runbook, user guide, acceptance mapping and spec deviations |

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- SQL Server 2019 or later. Development settings use the local default instance with Windows authentication (`Server=localhost`).

### Run the broker

```powershell
dotnet run --project src/MessageBroker.Api
```

In Development the broker creates the `BrokerDb` database and deploys the schema on startup, then listens on `http://localhost:5080`. The development bootstrap admin key is in `src/MessageBroker.Api/appsettings.Development.json`.

- API docs: `http://localhost:5080/swagger` or `http://localhost:5080/scalar`
- Health: `http://localhost:5080/health/ready`

To create your own admin key:

```powershell
dotnet run --project src/MessageBroker.Api -- new-api-key
```

Set the printed key as `Broker:Bootstrap:AdminApiKey`.

### Publish a message

```powershell
$broker = 'http://localhost:5080'
$h = @{ Authorization = "ApiKey <publisher-key>"; 'Idempotency-Key' = [guid]::NewGuid() }
Invoke-RestMethod -Method Post "$broker/api/v1/topics/payments/messages" -Headers $h `
  -ContentType application/json -Body '{"messageType":"PaymentCreated","payload":{"id":"p-1","amount":42}}'
```

See [docs/runbook.md](docs/runbook.md) for registering applications, issuing keys, creating topics and subscriptions, and granting permissions.

## Admin dashboard

```powershell
dotnet run --project src/MessageBroker.Dashboard      # http://localhost:5090
```

Sign in with an **Admin** API key (in development, the bootstrap key from `src/MessageBroker.Api/appsettings.Development.json`). The broker must be running; the dashboard reaches it at `Dashboard:BrokerUrl` (default `http://localhost:5080/`).

| Page | What it shows |
|---|---|
| **Overview** | Pending, leased and dead-lettered deliveries; a per-minute throughput chart; each subscription's backlog, webhook circuit state and connected SignalR clients |
| **Messages** | Search by topic, status, type, correlation ID, publisher and time; each message's payload, deliveries and attempt timeline |
| **Dead letters** | The DLQ across all subscriptions; requeue one or many |
| **Topology** | Topics, subscriptions and applications with their permissions (read-only) |

Pages update live through the broker's admin hub (`/hubs/admin`). If the hub can't be reached they fall back to polling, and the badge in the corner shows which. See [runbook section 9](docs/runbook.md#9-admin-dashboard) for deployment and access.

## Samples

The samples share one onboarding step, which registers the sample applications, topics and subscriptions with a running broker and writes their keys to `samples/samples.local.json` (git-ignored):

```powershell
dotnet run --project samples/SamplePublisher -- setup --AdminKey <admin-key>
```

Then run any of:

| Sample | What it shows |
|---|---|
| `samples/SamplePublisher` | Generates payments and publishes them through a transactional outbox |
| `samples/WebhookSubscriber` | Receives signed webhook deliveries |
| `samples/SignalRSubscriber` | Receives deliveries over SignalR |
| `samples/PullSubscriber` | Leases and settles deliveries over REST |
| `samples/BlazorPublisher` / `samples/BlazorSubscriber` | Sends notifications on the `notifications` topic and shows them as toasts (`http://localhost:5082`, `http://localhost:5083`) |
| `samples/ConsolePublisher` / `samples/ConsoleSubscriber` | Sends notifications from a prompt (`Title \| message \| level`) and prints each one as it arrives over SignalR, Pull and Webhook (`http://localhost:5084`) |

```powershell
dotnet run --project samples/SamplePublisher -- --Generator:Count 20 --Generator:FailEvery 5
```

## Tests

```powershell
dotnet test
```

- **Unit tests** and **dashboard tests** (bUnit) need nothing external.
- **Integration tests** create throwaway databases on SQL Server. They use the local default instance with Windows authentication unless `BROKER_TEST_SQL` holds another server connection string.
- **Load test** (NBomber) is a console app that hosts its own broker:

  ```powershell
  dotnet run -c Release --project tests/MessageBroker.LoadTests
  ```

## Configuration

Settings live in the `Broker` section of `appsettings.json`; any of them can be overridden with environment variables such as `Broker__Webhooks__TimeoutSeconds=30`.

| Setting | Default | Purpose |
|---|---|---|
| `ConnectionStrings:BrokerDb` | — | SQL Server connection string |
| `Broker:Bootstrap:AdminApiKey` | — | Ensures an admin application with this key exists at startup |
| `Broker:DataProtection:KeysDirectory` | — | Where the key ring for webhook secrets is stored. **Required in production.** |
| `Broker:Database:DeploySchemaOnStartup` | `true` | Run DbUp on start; set `false` when DBAs apply the release script |
| `Broker:Defaults:*` | 4 attempts, 60 s lock, 30–900 s retry, 8 concurrent | Defaults for new subscriptions |
| `Broker:Webhooks:*` | 30 s timeout, HTTPS required, circuit 5 failures / 60 s | Webhook delivery |
| `Broker:Retention:*` | 14 days completed, 90 days dead letters | Purge of old data |

## Documentation

- [Operations runbook](docs/runbook.md): installation, deployment, monitoring, onboarding and troubleshooting
- [User guide](docs/user-guide/MessageBroker-User-Guide.pdf) ([HTML](docs/user-guide/user-guide.html))
- [Acceptance criteria](docs/acceptance.md): how each acceptance criterion is tested
- [Spec deviations](docs/spec-deviations.md): where and why the build departs from the Phase 1 specification

## Limitations (Phase 1)

- Run **one** broker instance per database. Circuit breakers and the SignalR connection registry are held in memory.
- Delivery is at-least-once; subscribers should handle duplicates idempotently.
