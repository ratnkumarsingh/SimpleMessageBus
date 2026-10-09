# ActiveBatch samples

Two ways for a scheduled job to publish events to the broker. Both work the same with ActiveBatch, Windows Task Scheduler or SQL Server Agent: the job runs a command, and its **exit code** says what happened.

| Exit code | Meaning | What the job should do |
|---|---|---|
| `0` | Done. Also when the event was already published earlier with the same key. | Succeed. |
| `1` | Refused: bad settings, bad key, unknown topic, invalid event or rejected outbox rows. A retry will not help. | Fail and alert someone. |
| `2` | The broker could not be reached or stayed unavailable. For `relay-once` also a wrong key or topic: the rows wait, and the printed warning says why. | Retry later (nothing was lost); alert if it keeps happening. |

## Way A: the job publishes its own event (`Publish-BrokerEvent.ps1`)

Use this when the job itself is the producer, for example "the nightly import finished".

```powershell
$env:BROKER_API_KEY = '<publisher key>'          # from the scheduler's secure store, never in the script
.\Publish-BrokerEvent.ps1 -BrokerUrl https://broker.internal/ -Topic notifications `
    -MessageType UserNotification -IdempotencyKey "nightly-import-2026-10-09" `
    -Payload '{"title":"Nightly import","text":"1,250 rows loaded","level":"Success","sentBy":"ActiveBatch"}'
```

- **Idempotency key:** name the business event, not the run. A job rerun on the same day uses the same key, so it is not published twice (`nightly-import-<date>`, `invoice-batch-<batchId>`).
- Retries 408, 429, 5xx and connection errors 3 times (2, 4 s apart) with the same key before exiting `2`.
- Runs on Windows PowerShell 5.1 and PowerShell 7. It never prints the key.
- **From a plain command line** (cmd, or a job step that runs a program), put the JSON in a file and pass `-PayloadFile`. Quotes inside JSON do not survive a Windows command line:
  ```
  powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File Publish-BrokerEvent.ps1 ^
    -BrokerUrl https://broker.internal/ -Topic notifications -MessageType UserNotification ^
    -IdempotencyKey nightly-import-2026-10-09 -PayloadFile nightly-import.json
  ```

`Example-NightlyJob.ps1` shows a whole job: it does its work, then publishes a `UserNotification` to `notifications`. The notification appears in the samples' ConsoleSubscriber and BlazorSubscriber.

## Way B: the job sends what stored procedures queued (`SamplePublisher relay-once`)

Use this when events are written by stored procedures into an outbox table (`sample.usp_Outbox_Enqueue`, see `samples/Samples.Shared/sample.sql`). The job sends every pending row and exits, so no always-running service is needed.

```powershell
SamplePublisher.exe relay-once        # or: dotnet run --project samples/SamplePublisher -- relay-once
# relay-once: 12 sent, 0 rejected
```

Schedule it every minute. Each row is sent with the key `outbox-<OutboxId>`, so a run that was cut off, or two relays running at the same time, never publish a row twice.

## Setting up the jobs in ActiveBatch

Exact menu names differ between ActiveBatch versions; the settings to look for are the same.

1. **Credentials.** Store the broker API key in ActiveBatch's secure store (a secure/hidden variable or a credential object). Pass it to the job as the environment variable `BROKER_API_KEY`. Do not put it on the command line, where it shows up in job history.
2. **Way A job:** usually the last step of the job that does the work. Either:
   - a PowerShell script step that builds the payload and calls the script, as `Example-NightlyJob.ps1` does; or
   - a process step that runs `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <path>\Publish-BrokerEvent.ps1 … -PayloadFile <path>\event.json`.
3. **Way B job:** a process job that runs `SamplePublisher.exe relay-once` on a schedule of every 1 minute, with "do not start if the previous run is still running".
4. **Completion rules:** exit code `0` = success. Exit code `2` = failure with automatic restart (for example 3 restarts, 1 minute apart). Exit code `1` = failure with an alert, no restart.

## Keys for the samples

The local samples keep their keys in `samples.local.json` (written by `SamplePublisher setup`, or `setup-console` to add the console keys to an existing file):

- **Way A on `notifications`:** use `Samples:ConsolePublisher:ApiKey`.
- **Way B:** SamplePublisher reads `Samples:Publisher:ApiKey` itself.

In production, ask the broker admin for a key with Publish permission on your topic.
