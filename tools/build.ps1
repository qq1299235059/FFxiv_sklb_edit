param([switch]$Test, [switch]$Probe, [switch]$Release)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/cli-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:APPDATA = Join-Path $projectRoot '.tools/appdata'
$dalamudLib = Join-Path $env:USERPROFILE 'AppData/Roaming/XIVLauncherCN/addon/Hooks/dev'
$dotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'Run python tools/bootstrap_sdk.py first.' }
if ($Test) {
    $testOutput=Join-Path $projectRoot 'outputs/tests'
    New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
    & $dotnet run --project (Join-Path $projectRoot 'tests/ClothingSkeleton.Tests') | Tee-Object -FilePath (Join-Path $testOutput 'core-checks.txt')
} elseif ($Probe) {
    & $dotnet build (Join-Path $projectRoot 'tools/EnvironmentProbe') "-p:DalamudLibPath=$dalamudLib" --nologo
} else {
    if ($Release) {
        & $dotnet build (Join-Path $projectRoot 'src/ClothingSkeleton.Plugin') -c Release -p:DebugType=none -p:DebugSymbols=false "-p:DalamudLibPath=$dalamudLib" --nologo
    } else {
        & $dotnet build (Join-Path $projectRoot 'src/ClothingSkeleton.Plugin') "-p:DalamudLibPath=$dalamudLib" --nologo
    }
}
exit $LASTEXITCODE
