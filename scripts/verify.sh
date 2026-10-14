#!/usr/bin/env bash
# One-command verification: restore, build (warnings are errors) and run every test suite.
#
#   ./scripts/verify.sh
#
# Integration and concurrency tests need a real PostgreSQL. By default they start one with
# Testcontainers (Docker required). Without Docker, point them at any PostgreSQL 15+ server you control:
#   LEDGERCORE_TEST_POSTGRES="Host=127.0.0.1;Port=5432;Username=postgres;Password=...;Database=postgres" ./scripts/verify.sh
set -euo pipefail
cd "$(dirname "$0")/.."

echo "== restore"; dotnet restore LedgerCore.slnx
echo "== build";   dotnet build LedgerCore.slnx -c Release --no-restore
echo "== unit tests";        dotnet test tests/LedgerCore.UnitTests -c Release --no-build
echo "== integration tests"; dotnet test tests/LedgerCore.IntegrationTests -c Release --no-build
echo "== concurrency tests"; dotnet test tests/LedgerCore.ConcurrencyTests -c Release --no-build
echo "All suites passed."
