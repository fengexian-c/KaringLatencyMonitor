param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
$workspaceDotnet = Join-Path (Split-Path $projectRoot -Parent | Split-Path -Parent) "work\.dotnet\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $workspaceDotnet) { $workspaceDotnet } else { "dotnet" }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot ".dotnet-home"
$env:NUGET_PACKAGES = Join-Path $projectRoot ".nuget\packages"
$env:APPDATA = Join-Path $projectRoot ".appdata"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
New-Item -ItemType Directory -Path $env:APPDATA -Force | Out-Null

& $dotnet restore (Join-Path $projectRoot "src\KaringLatencyMonitor.App\KaringLatencyMonitor.App.csproj") `
    --configfile (Join-Path $projectRoot "NuGet.Config") `
    -p:Platform=$Platform

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet build (Join-Path $projectRoot "src\KaringLatencyMonitor.App\KaringLatencyMonitor.App.csproj") `
    -c $Configuration `
    -p:Platform=$Platform `
    --no-restore

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
