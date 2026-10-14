<#
.SYNOPSIS
    Runs a throwaway PostgreSQL 17 cluster for local development and tests on machines without Docker.

.DESCRIPTION
    Docker + Testcontainers is the primary way to run the integration tests. This script exists for
    environments where Docker is unavailable (for example a locked-down Windows workstation). It uses the
    official EDB PostgreSQL binaries extracted to a user-writable folder and never requires admin rights.

    The cluster listens on 127.0.0.1 only. The superuser password is generated randomly on 'init' and
    stored in the tools folder inside the current user's profile; it is never written to the repository.

.EXAMPLE
    ./scripts/local-postgres.ps1 init
    ./scripts/local-postgres.ps1 start
    $env:LEDGERCORE_TEST_POSTGRES = ./scripts/local-postgres.ps1 connection-string
    dotnet test
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('init', 'start', 'stop', 'status', 'connection-string', 'destroy')]
    [string] $Action,

    [int] $Port = 55432
)

$ErrorActionPreference = 'Stop'

$toolsRoot = if ($env:LEDGERCORE_TOOLS_DIR) { $env:LEDGERCORE_TOOLS_DIR } else { Join-Path $env:LOCALAPPDATA 'LedgerCoreTools' }
$binDir = if ($env:LEDGERCORE_PG_BIN) { $env:LEDGERCORE_PG_BIN } else { Join-Path $toolsRoot 'pgsql\bin' }
$dataDir = Join-Path $toolsRoot 'pgdata'
$logFile = Join-Path $toolsRoot 'postgres.log'
$passwordFile = Join-Path $toolsRoot 'pg-password.txt'

function Get-PgTool([string] $name) {
    $path = Join-Path $binDir "$name.exe"
    if (-not (Test-Path $path)) { throw "PostgreSQL tool '$path' not found. Set LEDGERCORE_PG_BIN to the PostgreSQL bin directory." }
    return $path
}

switch ($Action) {
    'init' {
        if (Test-Path (Join-Path $dataDir 'PG_VERSION')) { Write-Host "Cluster already initialised at $dataDir"; break }
        New-Item -ItemType Directory -Force $toolsRoot | Out-Null
        # 32 random bytes -> URL-safe base64 password. Stored only in the user's profile.
        $bytes = New-Object byte[] 32
        [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $password = [Convert]::ToBase64String($bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')
        Set-Content -Path $passwordFile -Value $password -NoNewline -Encoding ascii
        & (Get-PgTool 'initdb') -D $dataDir -U postgres --auth=scram-sha-256 --pwfile=$passwordFile -E UTF8 --no-locale | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "initdb failed with exit code $LASTEXITCODE" }
        # Enough connections for the multi-instance concurrency tests (two API hosts, each with its own pool).
        Add-Content -Path (Join-Path $dataDir 'postgresql.conf') -Value @"

# --- LedgerCore local development settings ---
listen_addresses = '127.0.0.1'
port = $Port
max_connections = 300
"@
        Write-Host "Initialised cluster at $dataDir (port $Port)."
    }
    'start' {
        # Start-Process (instead of '&') so the long-running postgres child does not inherit this
        # shell's stdout pipe; otherwise callers that capture output would wait forever.
        $process = Start-Process -FilePath (Get-PgTool 'pg_ctl') -ArgumentList @('-D', "`"$dataDir`"", '-l', "`"$logFile`"", '-w', 'start') -WindowStyle Hidden -PassThru
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "pg_ctl start failed; see $logFile" }
        Write-Host "PostgreSQL started on 127.0.0.1:$Port (log: $logFile)"
    }
    'stop' {
        & (Get-PgTool 'pg_ctl') -D $dataDir -m fast -w stop | Out-Host
    }
    'status' {
        & (Get-PgTool 'pg_ctl') -D $dataDir status | Out-Host
    }
    'connection-string' {
        if (-not (Test-Path $passwordFile)) { throw "No password file found; run 'init' first." }
        $password = Get-Content $passwordFile -Raw
        # Admin connection string used by the test fixture to create one database per test.
        Write-Output "Host=127.0.0.1;Port=$Port;Username=postgres;Password=$password;Database=postgres"
    }
    'destroy' {
        & (Get-PgTool 'pg_ctl') -D $dataDir -m fast -w stop 2>$null | Out-Null
        if (Test-Path $dataDir) { Remove-Item -Recurse -Force $dataDir }
        if (Test-Path $passwordFile) { Remove-Item -Force $passwordFile }
        Write-Host "Removed local cluster."
    }
}
