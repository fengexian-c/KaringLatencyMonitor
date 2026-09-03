param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [ValidateSet("portable", "lean")]
    [string]$Variant = "portable"
)

$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
$workspaceDotnet = Join-Path (Split-Path $projectRoot -Parent | Split-Path -Parent) "work\.dotnet\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $workspaceDotnet) { $workspaceDotnet } else { "dotnet" }
$platform = if ($Runtime -eq "win-arm64") { "ARM64" } else { "x64" }
$appProject = Join-Path $projectRoot "src\KaringLatencyMonitor.App\KaringLatencyMonitor.App.csproj"
$artifactRoot = Join-Path $projectRoot "artifacts"
$artifactName = if ($Variant -eq "lean") {
    "KaringLatencyMonitor-$Runtime-lean"
} else {
    "KaringLatencyMonitor-$Runtime"
}
$windowsAppSdkSelfContained = if ($Variant -eq "portable") { "true" } else { "false" }
$publishDirectory = Join-Path $artifactRoot $artifactName
$stagingDirectory = Join-Path $artifactRoot ".package-$Runtime-$Variant"
$archivePath = "$publishDirectory.zip"

$resolvedProjectRoot = [System.IO.Path]::GetFullPath($projectRoot)
$resolvedPublishDirectory = [System.IO.Path]::GetFullPath($publishDirectory)
$resolvedStagingDirectory = [System.IO.Path]::GetFullPath($stagingDirectory)
$resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
if (-not $resolvedPublishDirectory.StartsWith($resolvedArtifactRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to package outside the project artifacts directory."
}
if (-not $resolvedStagingDirectory.StartsWith($resolvedArtifactRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to stage outside the project artifacts directory."
}

$env:DOTNET_CLI_HOME = Join-Path $projectRoot ".dotnet-home"
$env:NUGET_PACKAGES = Join-Path $projectRoot ".nuget\packages"
$env:APPDATA = Join-Path $projectRoot ".appdata"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
New-Item -ItemType Directory -Path $env:APPDATA -Force | Out-Null
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

if (Test-Path -LiteralPath $stagingDirectory) {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

& $dotnet restore $appProject `
    --configfile (Join-Path $projectRoot "NuGet.Config") `
    -r $Runtime `
    -p:Platform=$platform `
    -p:SelfContained=true `
    -p:PublishAot=true `
    -p:PublishTrimmed=true `
    -p:WindowsAppSDKSelfContained=$windowsAppSdkSelfContained
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet publish $appProject `
    -c Release `
    -r $Runtime `
    -p:Platform=$platform `
    -p:SelfContained=true `
    -p:PublishAot=true `
    -p:PublishTrimmed=true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -p:OptimizationPreference=Size `
    -p:IlcFoldIdenticalMethodBodies=true `
    -p:WindowsAppSDKSelfContained=$windowsAppSdkSelfContained `
    -p:CopyOutputSymbolsToPublishDirectory=false `
    --no-restore `
    -o $stagingDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$requiredFiles = @(
    "KaringLatencyMonitor.App.exe",
    "KaringLatencyMonitor.App.pri",
    "App.xbf",
    "MainWindow.xbf",
    "Controls\HeatmapStrip.xbf"
)
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory $requiredFile))) {
        throw "Publish output is incomplete: missing $requiredFile"
    }
}

Copy-Item -LiteralPath (Join-Path $projectRoot "DISTRIBUTION.md") `
    -Destination (Join-Path $stagingDirectory "使用说明.md")
Compress-Archive -Path (Join-Path $stagingDirectory "*") `
    -DestinationPath $archivePath `
    -CompressionLevel Optimal

# The extracted directory is also used as the directly runnable test copy. Keep
# its data directory intact while replacing program files. If that exact EXE is
# running, leave the directory untouched and still produce the new ZIP package.
$publishedExe = Join-Path $publishDirectory "KaringLatencyMonitor.App.exe"
$isPublishedCopyRunning = Get-Process -Name "KaringLatencyMonitor.App" -ErrorAction SilentlyContinue |
    Where-Object {
        try {
            [string]::Equals(
                [System.IO.Path]::GetFullPath($_.Path),
                [System.IO.Path]::GetFullPath($publishedExe),
                [System.StringComparison]::OrdinalIgnoreCase)
        }
        catch {
            $false
        }
    } |
    Select-Object -First 1

if ($isPublishedCopyRunning) {
    Write-Warning "The directly runnable copy is active; its files and data were left untouched. Exit it and run package.ps1 again to update that copy."
}
else {
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $publishDirectory -Force |
        Where-Object { $_.Name -ne "data" } |
        Remove-Item -Recurse -Force
    Copy-Item -Path (Join-Path $stagingDirectory "*") `
        -Destination $publishDirectory `
        -Recurse `
        -Force
}

Remove-Item -LiteralPath $stagingDirectory -Recurse -Force

$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Package: $archivePath"
Write-Host "SHA256: $hash"
