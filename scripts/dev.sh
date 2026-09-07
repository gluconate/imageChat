#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")/.."
export DOTNET_CLI_HOME="$PWD/.cache/dotnet"
export NUGET_PACKAGES="$PWD/.cache/nuget"
export NUGET_HTTP_CACHE_PATH="$PWD/.cache/nuget-http"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export TMPDIR="$PWD/.cache/tmp"
mkdir -p "$TMPDIR"
case "${1:-run}" in
  create-admin) dotnet run --project ImageChat.csproj -c Release --no-launch-profile -- --create-admin ;;
  reset-admin-password) dotnet run --project ImageChat.csproj -c Release --no-launch-profile -- --reset-admin-password ;;
  run) dotnet run --project ImageChat.csproj --no-launch-profile ;;
  test) dotnet test tests/ImageChat.Tests.csproj -c Release ;;
  build) dotnet build ImageChat.csproj ;;
  publish) dotnet publish ImageChat.csproj -c Release -o .cache/publish ;;
  *) echo 'Usage: sh scripts/dev.sh [run|test|build|publish|create-admin|reset-admin-password]' >&2; exit 1 ;;
esac