<#
.SYNOPSIS
    Writes the broker schema as one reviewable SQL script for DBAs.
.DESCRIPTION
    The script brings a database at any version up to date. Each migration runs only when
    broker.SchemaVersions has no row for it, and procedures, views and functions are re-applied.
    It is built from the same embedded scripts, in the same order, as the DbUp deploy at startup.
    Apply it with: sqlcmd -b -S <server> -d <database> -i <file>
.EXAMPLE
    ./db/build-release-script.ps1 -OutFile artifacts/broker-release.sql
#>
param(
    [string] $OutFile = (Join-Path $PSScriptRoot '..' 'artifacts' 'broker-release.sql')
)

$ErrorActionPreference = 'Stop'
$OutFile = [System.IO.Path]::GetFullPath($OutFile)
New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null

$project = Join-Path $PSScriptRoot '..' 'src' 'MessageBroker.Api'
dotnet run --project $project --no-launch-profile -- release-script $OutFile
if ($LASTEXITCODE -ne 0) { throw "dotnet run failed with exit code $LASTEXITCODE" }
