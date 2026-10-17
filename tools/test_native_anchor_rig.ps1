param([Parameter(Mandatory=$true)][string]$Blender)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectRoot
$plugin=Join-Path $projectRoot 'src/ClothingSkeleton.Plugin/bin/Debug'
$out=Join-Path $projectRoot ('outputs/anchor-rig-check/trial-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $out -Force | Out-Null
# This original sample proves that the new descriptor and roots-first layout are
# used by the game itself; it is never installed on an actor by this test.
python tools/runtime_request.py inspect --path 'chara/human/c0201/skeleton/top/t0377/skl_c0201t0377.sklb'
if($LASTEXITCODE -ne 0){throw 'Original multi-root sample round trip failed.'}
$settings=Get-Content -LiteralPath (Join-Path $plugin 'workshop-settings.json') -Raw | ConvertFrom-Json
$multi=Get-Content -LiteralPath (Join-Path (Join-Path $plugin $settings.ProjectDirectory) 'anchor-rig.json') -Raw | ConvertFrom-Json
if(-not $multi.available -or $multi.existingAnchors.Count -ne 2){throw 'Original multi-root descriptor was not recognized.'}
python tools/runtime_request.py inspect --path 'chara/human/c0201/skeleton/top/t0100/skl_c0201t0100.sklb'
if($LASTEXITCODE -ne 0){throw 'Original Odin skeleton round trip failed.'}
$settings=Get-Content -LiteralPath (Join-Path $plugin 'workshop-settings.json') -Raw | ConvertFrom-Json
$project=Join-Path $plugin $settings.ProjectDirectory
$session=Join-Path $plugin 'BlenderSession/anchor-rig-test'
New-Item -ItemType Directory -Path (Join-Path $session 'tmp') -Force | Out-Null
$env:BLENDER_USER_RESOURCES=$session
$env:TEMP=Join-Path $session 'tmp'
$env:TMP=$env:TEMP
& $Blender --background --factory-startup --python-exit-code 1 --python tools/blender_anchor_rig_smoke.py -- $project $plugin (Join-Path $out 'blender-report.json') *> (Join-Path $out 'blender.log')
if($LASTEXITCODE -ne 0){throw 'Full Blender anchor rig check failed; see private test log.'}
python tools/runtime_request.py append
if($LASTEXITCODE -ne 0){throw 'Native multi-anchor conversion failed.'}
$settings=Get-Content -LiteralPath (Join-Path $plugin 'workshop-settings.json') -Raw | ConvertFrom-Json
$candidate=Join-Path $plugin $settings.CandidatePath
$validationFile=Join-Path (Split-Path $candidate -Parent) 'validation.json'
$validation=Get-Content -LiteralPath $validationFile -Raw | ConvertFrom-Json
$raw=[IO.File]::ReadAllBytes($candidate)
$connections=0..3 | ForEach-Object {[BitConverter]::ToInt16($raw,40+2*$_)}
if([BitConverter]::ToUInt16($raw,4) -ne 0x3031 -or ($connections -join ',') -ne '22,64,65,-1'){throw 'Native multi-root parent descriptor mismatch.'}
if($validation.candidateBoneCount -ne 20 -or $validation.authoredBoneCount -ne 8 -or $validation.usedAnchorCount -ne 2 -or -not $validation.originalBoneTransformsPreserved){throw 'Unexpected multi-anchor candidate counts or changed original rest poses.'}
if(-not $validation.anchorRig.originalLayerIndicesRemapped -or ($validation.layers.Id -join ',') -ne '4,5,25'){throw 'Native layer index remapping failed.'}
Copy-Item -LiteralPath $validationFile -Destination (Join-Path $out 'native-validation.json')
python tools/runtime_request.py est-package
if($LASTEXITCODE -ne 0){throw 'Equipment-only package generation failed.'}
python tools/runtime_request.py preview-selftest
if($LASTEXITCODE -ne 0){throw 'Unassigned Penumbra registration rollback failed.'}
$report=@{state='passed';version='0.4.0';originalSqPackOnly=$true;originalMultiRootSampleVerified=$true;originalMultiRootConnectionCount=2;
    selectableBodyAnchorCount=106;usedAnchorCount=2;ignoredAnchorCount=104;authoredBoneCount=8;originalBoneCount=10;candidateBoneCount=20;
    manualParentingVerified=$true;nativeFileConversionVerified=$true;originalRestAndParentRelationshipsPreserved=$true;
    originalLayerIndicesRemapped=$true;connections=@(22,64,65);packageInstalled=$false;actorPreviewApplied=$false;gameMotionVerified=$false;
    coreChecksPassed=[int]([regex]::Match((Get-Content -LiteralPath 'outputs/tests/core-checks.txt' -Raw),'(\d+) checks passed').Groups[1].Value)}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out 'report.json') -Encoding utf8
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path (Split-Path $out -Parent) 'report.json') -Encoding utf8
Write-Output 'Full body anchor rig: manual parenting, unused pruning, preserved originals, native multi-root conversion and unassigned rollback passed. Character motion remains unverified.'
