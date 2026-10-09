<#
.SYNOPSIS
    An example ActiveBatch job: do the job's work, then tell the rest of the company it finished.

.DESCRIPTION
    Publishes a UserNotification to the "notifications" topic, so it shows up in the samples'
    ConsoleSubscriber and BlazorSubscriber. The Idempotency-Key is built from the job name and the
    business date, so a rerun on the same day (retry or manual rerun) does not notify twice.

    The job's exit code is Publish-BrokerEvent's: 0 done, 1 refused (fix settings), 2 broker unavailable (retry).

.EXAMPLE
    $env:BROKER_API_KEY = '<publisher key with Publish on notifications>'
    .\Example-NightlyJob.ps1 -BrokerUrl http://localhost:5080/
#>
[CmdletBinding()]
param(
    [Uri]    $BrokerUrl = 'http://localhost:5080/',
    [string] $Topic = 'notifications',
    [string] $JobName = 'nightly-import',
    # The day the job's data belongs to (not the time it runs), so reruns reuse the same key.
    [datetime] $BusinessDate = (Get-Date).Date
)

$ErrorActionPreference = 'Stop'

# 1. The job's own work. Stand-in: pretend to import some rows.
$rows = Get-Random -Minimum 1000 -Maximum 5000
Write-Output "Imported $rows rows for $($BusinessDate.ToString('yyyy-MM-dd'))"

# 2. Publish the "finished" event. ConvertTo-Json writes the payload; -Compress keeps it on one line.
$payload = @{
    title  = 'Nightly import finished'
    text   = "$rows rows loaded for $($BusinessDate.ToString('yyyy-MM-dd'))"
    level  = 'Success'
    sentBy = "ActiveBatch:$JobName"
} | ConvertTo-Json -Compress

& (Join-Path $PSScriptRoot 'Publish-BrokerEvent.ps1') `
    -BrokerUrl $BrokerUrl -Topic $Topic -MessageType 'UserNotification' -Payload $payload `
    -IdempotencyKey "$JobName-$($BusinessDate.ToString('yyyy-MM-dd'))" `
    -CorrelationId "$JobName-$($BusinessDate.ToString('yyyyMMdd'))"
exit $LASTEXITCODE
