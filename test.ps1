$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
$workspaceDotnet = Join-Path (Split-Path $projectRoot -Parent | Split-Path -Parent) "work\.dotnet\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $workspaceDotnet) { $workspaceDotnet } else { "dotnet" }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot ".dotnet-home"
$env:NUGET_PACKAGES = Join-Path $projectRoot ".nuget\packages"
$env:APPDATA = Join-Path $projectRoot ".appdata"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
New-Item -ItemType Directory -Path $env:APPDATA -Force | Out-Null

& $dotnet restore `
    (Join-Path $projectRoot "tests\KaringLatencyMonitor.Core.Tests\KaringLatencyMonitor.Core.Tests.csproj") `
    --configfile (Join-Path $projectRoot "NuGet.Config")

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet run `
    --project (Join-Path $projectRoot "tests\KaringLatencyMonitor.Core.Tests\KaringLatencyMonitor.Core.Tests.csproj") `
    -c Release `
    --no-restore
exit $LASTEXITCODE
