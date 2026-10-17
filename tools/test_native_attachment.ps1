param([Parameter(Mandatory=$true)][string]$Blender)
& (Join-Path $PSScriptRoot 'test_native_anchor_rig.ps1') -Blender $Blender
exit $LASTEXITCODE
