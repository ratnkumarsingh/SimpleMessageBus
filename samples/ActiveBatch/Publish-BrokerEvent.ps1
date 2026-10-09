<#
.SYNOPSIS
    Publishes one event to the message broker. Made for scheduler jobs (ActiveBatch, Task Scheduler, SQL Agent).

.DESCRIPTION
    POSTs the event to /api/v1/topics/{Topic}/messages with an Idempotency-Key, so running the job again
    (a retry, or a rerun by an operator) never publishes the same event twice. Retries 408, 429, 5xx and
    network errors with backoff (2, 4, 8 s ...), always with the same key.

    Exit codes, for the job's success and retry rules:
      0  Published. Also 0 when the broker already had this Idempotency-Key (HTTP 200).
      1  Refused (400, 401, 403, 404, 413 ...). A retry will not help: fix the job or its settings.
      2  The broker could not be reached or stayed unavailable. Safe to retry the job later.

    Works with Windows PowerShell 5.1 and PowerShell 7. The API key is never printed.

    Pass the event as -Payload when calling from PowerShell (a script job). From a plain command line
    (cmd, a process job), use -PayloadFile instead: quotes inside JSON do not survive a Windows command line.

.EXAMPLE
    $env:BROKER_API_KEY = '<from the scheduler''s secure store>'
    .\Publish-BrokerEvent.ps1 -BrokerUrl https://broker.internal/ -Topic notifications `
        -MessageType UserNotification -IdempotencyKey "nightly-import-2026-10-09" `
        -Payload '{"title":"Nightly import","text":"1,250 rows loaded","level":"Success","sentBy":"ActiveBatch"}'

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File Publish-BrokerEvent.ps1 -BrokerUrl https://broker.internal/ ^
        -Topic notifications -MessageType UserNotification -IdempotencyKey nightly-import-2026-10-09 -PayloadFile payload.json
#>
[CmdletBinding(DefaultParameterSetName = 'Inline')]
param(
    [Parameter(Mandatory)] [Uri]    $BrokerUrl,
    [Parameter(Mandatory)] [string] $Topic,
    [Parameter(Mandatory)] [string] $MessageType,
    # The event's JSON: as text (-Payload), or the path of a UTF-8 file that holds it (-PayloadFile).
    [Parameter(Mandatory, ParameterSetName = 'Inline')] [string] $Payload,
    [Parameter(Mandatory, ParameterSetName = 'File')] [string] $PayloadFile,
    # Names the business event, not the attempt: e.g. "nightly-import-2026-10-09". At most 100 characters.
    [Parameter(Mandatory)] [ValidateLength(1, 100)] [string] $IdempotencyKey,
    [string] $CorrelationId,
    # Defaults to the BROKER_API_KEY environment variable, so the key stays out of the job's command line.
    [string] $ApiKey = $env:BROKER_API_KEY,
    [ValidateRange(1, 10)] [int] $MaxAttempts = 3,
    [ValidateRange(1, 300)] [int] $TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is much slower with the progress bar in 5.1

function Write-Result([string] $Text) { Write-Output "Publish-BrokerEvent: $Text" }

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    Write-Result 'No API key. Set the BROKER_API_KEY environment variable or pass -ApiKey.'
    exit 1
}
if ($PayloadFile) {
    if (-not (Test-Path -LiteralPath $PayloadFile -PathType Leaf)) {
        Write-Result "-PayloadFile '$PayloadFile' was not found."
        exit 1
    }
    $Payload = (Get-Content -LiteralPath $PayloadFile -Raw -Encoding UTF8).Trim()
}
try { $null = $Payload | ConvertFrom-Json }
catch {
    Write-Result "The payload is not valid JSON: $($_.Exception.Message)"
    exit 1
}

# Windows PowerShell 5.1 may not offer TLS 1.2 by default.
if ($PSVersionTable.PSVersion.Major -lt 6) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}

# The payload is inserted as it is, so its JSON is sent exactly as the job wrote it.
# Parenthesised because PowerShell's comma binds tighter than +.
$fields = @(('"messageType":' + (ConvertTo-Json $MessageType)), ('"payload":' + $Payload))
if ($CorrelationId) { $fields += ('"correlationId":' + (ConvertTo-Json $CorrelationId)) }
$body = [Text.Encoding]::UTF8.GetBytes('{' + ($fields -join ',') + '}')

$uri = $BrokerUrl.AbsoluteUri.TrimEnd('/') + "/api/v1/topics/$([Uri]::EscapeDataString($Topic))/messages"
$headers = @{ Authorization = "ApiKey $ApiKey"; 'Idempotency-Key' = $IdempotencyKey }

for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    $status = $null
    $detail = $null
    try {
        $response = Invoke-WebRequest -Uri $uri -Method Post -Headers $headers -Body $body `
            -ContentType 'application/json; charset=utf-8' -TimeoutSec $TimeoutSeconds -UseBasicParsing
        $result = $response.Content | ConvertFrom-Json
        if ([int] $response.StatusCode -eq 200) {
            Write-Result "already published earlier with key '$IdempotencyKey' as message $($result.messageId)"
        } else {
            Write-Result "published message $($result.messageId) to '$Topic' ($($result.deliveryCount) subscription(s))"
        }
        exit 0
    }
    catch {
        # 5.1 throws WebException, 7 throws HttpResponseException; both carry the response, if any.
        if ($_.Exception.Response) { $status = [int] $_.Exception.Response.StatusCode }
        $detail = if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { $_.Exception.Message }
    }

    $retryable = ($null -eq $status) -or ($status -in 408, 429) -or ($status -ge 500)
    if (-not $retryable) {
        Write-Result "refused with HTTP $status (not retried): $detail"
        exit 1
    }
    $what = if ($status) { "HTTP $status" } else { 'no connection' }
    if ($attempt -lt $MaxAttempts) {
        $delay = [Math]::Pow(2, $attempt)
        Write-Result "attempt $attempt of $MaxAttempts failed ($what); retrying in $delay s with the same key"
        Start-Sleep -Seconds $delay
    } else {
        Write-Result "broker unavailable after $MaxAttempts attempt(s) ($what): $detail"
        exit 2
    }
}
