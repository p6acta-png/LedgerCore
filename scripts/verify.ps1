<#
.SYNOPSIS
    One-command verification on Windows: restore, build (warnings are errors) and run every test suite.

.PARAMETER UseLocalPostgres
    Use the portable PostgreSQL managed by scripts/local-postgres.ps1 instead of Testcontainers/Docker.

.EXAMPLE
    ./scripts/verify.ps1                       # Docker available: Testcontainers starts PostgreSQL
    ./scripts/verify.ps1 -UseLocalPostgres     # no Docker: uses the local portable cluster
#>
param([switch] $UseLocalPostgres)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

if ($UseLocalPostgres) {
    & "$PSScriptRoot/local-postgres.ps1" status | Out-Null
    if ($LASTEXITCODE -ne 0) { & "$PSScriptRoot/local-postgres.ps1" start }
    $env:LEDGERCORE_TEST_POSTGRES = & "$PSScriptRoot/local-postgres.ps1" connection-string
}

function Step([string] $name, [scriptblock] $action) {
    Write-Host "== $name"
    & $action
    if ($LASTEXITCODE -ne 0) { throw "$name failed (exit code $LASTEXITCODE)" }
}

Step 'restore' { dotnet restore LedgerCore.slnx }
Step 'build' { dotnet build LedgerCore.slnx -c Release --no-restore }
Step 'unit tests' { dotnet test tests/LedgerCore.UnitTests -c Release --no-build }
Step 'integration tests' { dotnet test tests/LedgerCore.IntegrationTests -c Release --no-build }
Step 'concurrency tests' { dotnet test tests/LedgerCore.ConcurrencyTests -c Release --no-build }
Write-Host 'All suites passed.'
