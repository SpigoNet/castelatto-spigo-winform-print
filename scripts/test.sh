#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet test tests/ZplPrintManager.Core.Tests/ZplPrintManager.Core.Tests.csproj -c Release -p:RestoreLockedMode=true --logger trx --results-directory artifacts/test-results/core
dotnet test tests/ZplPrintManager.Infrastructure.Tests/ZplPrintManager.Infrastructure.Tests.csproj -c Release -p:RestoreLockedMode=true --logger trx --results-directory artifacts/test-results/infrastructure
