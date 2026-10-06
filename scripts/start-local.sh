#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build DigitalGoodsHub.slnx
ConnectionStrings__Sqlite="Data Source=$PWD/provider.db;Default Timeout=30" dotnet run --no-build --project src/DigitalGoodsHub.ProviderSimulator --urls http://localhost:5081 &
provider_pid=$!
trap 'kill "$provider_pid" 2>/dev/null || true' EXIT
ConnectionStrings__Sqlite="Data Source=$PWD/digitalgoods.db;Default Timeout=30" dotnet run --no-build --project src/DigitalGoodsHub.Api --urls http://localhost:5080
