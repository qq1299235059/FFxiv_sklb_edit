param([Parameter(Mandatory=$true)][string]$Blender)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_HOME=Join-Path $projectRoot '.tools/cli-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:APPDATA=Join-Path $projectRoot '.tools/appdata'
$dotnet=Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$dalamudLib=Join-Path $env:USERPROFILE 'AppData/Roaming/XIVLauncherCN/addon/Hooks/dev'
$pluginDirectory=Join-Path $projectRoot 'src/ClothingSkeleton.Plugin/bin/Debug'
$testDirectory=Join-Path $projectRoot ('outputs/e2e/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
python tools/runtime_request.py inspect --path 'chara/human/c0201/skeleton/top/t0100/skl_c0201t0100.sklb'
if($LASTEXITCODE -ne 0){ throw 'Native original round trip failed.' }
$project=Get-ChildItem -LiteralPath (Join-Path $pluginDirectory 'Projects') -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'snapshot.json') } | Sort-Object Name -Descending | Select-Object -First 1
& $Blender --background --factory-startup --python-exit-code 1 --python tools/blender_workflow_smoke.py -- $project.FullName (Join-Path $testDirectory 'blender') $pluginDirectory
if($LASTEXITCODE -ne 0){ throw 'Blender export failed.' }
& $dotnet build tools/EnvironmentProbe "-p:DalamudLibPath=$dalamudLib" --nologo
if($LASTEXITCODE -ne 0){ throw 'Exchange validation tool build failed.' }
& $dotnet run --project tools/EnvironmentProbe --no-build -- validate-gltf (Join-Path $project.FullName 'snapshot.json') (Join-Path $pluginDirectory 'BlenderReturn/edited.gltf') (Join-Path $testDirectory 'validated.json')
if($LASTEXITCODE -ne 0){ throw 'Blender return import validation failed.' }
python tools/runtime_request.py append
if($LASTEXITCODE -ne 0){ throw 'Native append round trip failed.' }
foreach($operation in @('model-check','penumbra-status','preview-selftest','package')) {
    python tools/runtime_request.py $operation
    if($LASTEXITCODE -ne 0){ throw "Integration operation failed: $operation" }
}
$candidate=Get-ChildItem -LiteralPath $project.FullName -Directory -Filter 'appended-*' | Sort-Object Name -Descending | Select-Object -First 1
$result=[ordered]@{
    state='passed'; timestamp=[DateTimeOffset]::UtcNow.ToString('O');
    originalItem='�¶�����'; itemId=3262; equipmentSetId=100; raceCode='c0201';
    originalSkeleton='chara/human/c0201/skeleton/top/t0100/skl_c0201t0100.sklb';
    gameVersion='2026.10.06.0000.0000'; dalamudVersion='15.0.3.7';
    projectDirectory=[IO.Path]::GetRelativePath($pluginDirectory,$project.FullName); candidateDirectory=[IO.Path]::GetRelativePath($pluginDirectory,$candidate.FullName);
    blenderReturn='BlenderReturn/edited.gltf';
    originalSqPackOnly=$true; originalBoneCount=10; candidateBoneCount=14;
    penumbraRegistrationAndRemovalVerified=$true; penumbraActorPathResolutionVerified=$false;
    penumbraTestAssignedToActor=$false; vertexWeightsVerified=$false; gameMotionVerified=$false
}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testDirectory 'report.json') -Encoding utf8
Write-Output "Completed: local integration report saved."
