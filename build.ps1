#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet run --project build/Build.csproj -- $args
