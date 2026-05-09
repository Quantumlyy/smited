#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"
exec dotnet run --project build/Build.csproj -- "$@"
