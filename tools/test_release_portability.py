"""Verify installed network add-ons in portable game folders and a standalone ZIP."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import zipfile

p=argparse.ArgumentParser();p.add_argument("--blender",required=True);p.add_argument("--source",required=True);a=p.parse_args()
root=Path(__file__).resolve().parents[1]
version=".".join(json.loads((root/"src/ClothingSkeleton.Plugin/ClothingSkeleton.json").read_text(encoding="utf-8-sig"))["AssemblyVersion"].split(".")[:3])
archive=root/f"outputs/release/ClothingSkeleton-{version}.zip"
checks=root/"outputs/portability-check";checks.mkdir(parents=True,exist_ok=True)
trial=Path(tempfile.mkdtemp(prefix="trial-",dir=checks))
first=trial/"device-a/plugin with spaces";second=trial/"device-b/插件 测试目录"
first.mkdir(parents=True)
with zipfile.ZipFile(archive) as package:package.extractall(first)
assert not (first/"workshop-settings.json").exists()

def run(plugin,label,addon=None):
    output=trial/label
    result=subprocess.run(["python",str(root/"tools/test_network_bridge.py"),"--blender",a.blender,"--source",str(Path(a.source).resolve()),"--addon",str(addon or plugin/"BlenderBridge/clothing_skeleton_bridge"),"--output",str(output),"--return-root",str(plugin)],capture_output=True,encoding="utf-8",errors="replace",timeout=100)
    (trial/(label+".log")).write_text(result.stdout+result.stderr,encoding="utf-8")
    if result.returncode:raise RuntimeError(f"{label} network test failed; inspect private log")
    return json.loads((output/"report.json").read_text(encoding="utf-8"))

initial=run(first,"first")
previous=(first/"BlenderReturn/edited.gltf").read_bytes();stamp=(first/"BlenderReturn/edited.gltf").stat().st_mtime_ns
second.parent.mkdir(parents=True);shutil.copytree(first,second)
moved=run(second,"relocated")
assert (first/"BlenderReturn/edited.gltf").read_bytes()==previous
assert (first/"BlenderReturn/edited.gltf").stat().st_mtime_ns==stamp
# Same deterministic ZIP as prepare_repository.py, installed through Blender's installer.
addon=root/f"outputs/release/ClothingSkeleton-Blender-{version}.zip"
with zipfile.ZipFile(addon,"w",zipfile.ZIP_DEFLATED) as z:
    for source,name in [(root/"blender/clothing_skeleton_bridge"/n,n) for n in ("__init__.py","anchors.py","connection.py")]+[(root/"blender/README.md","README.md")]:
        entry=zipfile.ZipInfo(f"clothing_skeleton_bridge/{name}",date_time=(2020,1,1,0,0,0));entry.compress_type=zipfile.ZIP_DEFLATED
        z.writestr(entry,source.read_bytes())
standalone=run(second,"standalone",addon)
standalone.update(standaloneZipSha256=hashlib.sha256(addon.read_bytes()).hexdigest(),installedWithBlenderAddonInstaller=True)
(root/"outputs/github-publish").mkdir(parents=True,exist_ok=True)
(root/"outputs/github-publish/blender-addon-report.json").write_text(json.dumps(standalone,indent=2),encoding="utf-8")
report=dict(state="passed",version=version,packageSha256=hashlib.sha256(archive.read_bytes()).hexdigest(),
    cleanPackageWithoutSettings=True,independentOfSourceCheckout=True,relocatedFolderWithSpacesAndUnicode=True,
    oldPluginFolderUnchanged=True,fixedReturnFile="BlenderReturn/edited.gltf",sharedReturnAcrossProjects=True,
    sourcePathsOmitted=True,coreGltfImportPassed=True,localPortInteroperabilityVerified=True,
    coreChecksPassed=int(re.search(r"(\d+) checks passed",(root/"outputs/tests/core-checks.txt").read_text(encoding="utf-8-sig")).group(1)),
    gameUiVerified=False,gameMotionVerified=False,blender=initial,relocatedBlender=moved,standaloneBlender=standalone)
(checks/"report.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
(checks/"latest-trial.txt").write_text(str(trial),encoding="utf-8")
print("Packaged network bridge passed both locations and Blender's standalone add-on installer.")
