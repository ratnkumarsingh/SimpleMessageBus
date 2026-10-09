<#
.SYNOPSIS
    Installs or updates the broker database with db/BrokerDb_Full.sql.
.DESCRIPTION
    Creates the database when it is missing, applies the schema, and adds the Admin application, its key
    and the webhook host allowlist. Running it again is safe: it applies only what is missing.
    The values reach sqlcmd as :setvar lines in a temporary file, because PowerShell mangles empty and
    quoted -v arguments on their way to sqlcmd.
.EXAMPLE
    $key = dotnet run --project src/MessageBroker.Api -- new-api-key
    ./db/install-database.ps1 -Server . -AdminApiKey $key -WebhookAllowedHosts hooks.example.com
#>
param(
    [string] $Server = '.',
    [string] $DatabaseName = 'BrokerDb',
    [string] $AdminName = 'broker-admin',
    [string] $AdminApiKey = '',
    [string[]] $WebhookAllowedHosts = @(),
    # Extra sqlcmd arguments, such as @('-U', 'deploy', '-P', $password). Integrated security by default.
    [string[]] $SqlcmdArgs = @()
)

$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'BrokerDb_Full.sql'
$values = [ordered]@{
    DatabaseName        = $DatabaseName
    AdminName           = $AdminName
    AdminApiKey         = $AdminApiKey
    WebhookAllowedHosts = $WebhookAllowedHosts -join ','
}

$wrapper = New-TemporaryFile
try {
    $lines = foreach ($name in $values.Keys) { ":setvar $name `"$($values[$name] -replace '"', '""')`"" }
    $lines += ":r `"$script`""
    Set-Content -Path $wrapper -Value $lines -Encoding utf8
    sqlcmd -b -S $Server @SqlcmdArgs -i $wrapper.FullName
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item $wrapper -ErrorAction SilentlyContinue
}
