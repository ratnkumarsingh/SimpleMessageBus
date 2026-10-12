<#
.SYNOPSIS
    Writes the broker schema as one reviewable SQL script for DBAs.
.DESCRIPTION
    The script brings a database at any version up to date. Each migration runs only when
    broker.SchemaVersions has no row for it, and procedures, views and functions are re-applied.
    It is built from the same embedded scripts, in the same order, as the DbUp deploy at startup.
    Apply it with: sqlcmd -b -S <server> -d <database> -i <file>

    With -Full it writes the full install script instead (default db/BrokerDb_Full.sql, which is
    committed): it also creates the database and adds the Admin application, its key and the webhook
    host allowlist from sqlcmd -v variables. The file header lists the variables. Regenerate it
    whenever a script under db/Migrations or db/Programmability changes.
.EXAMPLE
    ./db/build-release-script.ps1 -OutFile artifacts/broker-release.sql
.EXAMPLE
    ./db/build-release-script.ps1 -Full
#>
param(
    [string] $OutFile,
    [switch] $Full
)

$ErrorActionPreference = 'Stop'
if (-not $OutFile) {
    $OutFile = if ($Full) { Join-Path $PSScriptRoot 'BrokerDb_Full.sql' }
               else { Join-Path $PSScriptRoot '..' 'artifacts' 'broker-release.sql' }
}
$OutFile = [System.IO.Path]::GetFullPath($OutFile)
New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null

$project = Join-Path $PSScriptRoot '..' 'src' 'MessageBroker.Api'
$command = if ($Full) { 'full-script' } else { 'release-script' }
dotnet run --project $project --no-launch-profile -- $command $OutFile
if ($LASTEXITCODE -ne 0) { throw "dotnet run failed with exit code $LASTEXITCODE" }
