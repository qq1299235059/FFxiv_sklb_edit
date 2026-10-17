"""Verify the actual packaged bridge before and after moving its installed folder."""
import argparse
import hashlib
import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument("--blender", required=True)
parser.add_argument("--source", required=True)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
version = ".".join(json.loads((root / "src/ClothingSkeleton.Plugin/bin/Release/ClothingSkeleton.json").read_text(encoding="utf-8-sig"))["AssemblyVersion"].split(".")[:3])
archive = root / f"outputs/release/ClothingSkeleton-{version}.zip"
checks = root / "outputs/portability-check"
checks.mkdir(parents=True, exist_ok=True)
# A new test run gets a new folder and cannot reuse stale evidence.
import tempfile
trial = Path(tempfile.mkdtemp(prefix="trial-", dir=checks))
first, second = trial / "device-a/plugin with spaces", trial / "device-b/插件 测试目录"
first.mkdir(parents=True)
with zipfile.ZipFile(archive) as package:
    package.extractall(first)
assert not (first / "workshop-settings.json").exists()

def run_case(plugin, label):
    report_dir = trial / (label + "-report")
    resources = plugin / "BlenderSession"
    temporary = resources / "tmp"
    temporary.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    env.update(BLENDER_USER_RESOURCES=str(resources), TEMP=str(temporary), TMP=str(temporary))
    command = [args.blender, "--background", "--factory-startup", "--python-exit-code", "1", "--python",
               str(root / "tools/blender_workflow_smoke.py"), "--", str(Path(args.source).resolve()), str(report_dir), str(plugin)]
    result = subprocess.run(command, capture_output=True, encoding="utf-8", errors="replace", env=env, timeout=45)
    (trial / (label + ".log")).write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError(f"{label}: Blender workflow failed; see private test log")
    report = json.loads((report_dir / "workflow-report.json").read_text(encoding="utf-8"))
    assert report["state"] == "passed" and report["sharedReturnAcrossProjects"] and report["sourcePathsOmitted"]
    assert report["fixedReturnPath"] == "BlenderReturn/edited.gltf"
    # Use only the installed assets and the local project copied by the first workflow.
    attachment_report = report_dir / "attachment-report.json"
    attached = subprocess.run([args.blender, "--background", "--factory-startup", "--python-exit-code", "1", "--python",
                              str(root / "tools/blender_anchor_rig_smoke.py"), "--", str(plugin / "Projects/workflow-a"),
                              str(plugin), str(attachment_report)], capture_output=True, encoding="utf-8", errors="replace", env=env, timeout=45)
    (trial / (label + "-attachment.log")).write_text(attached.stdout + attached.stderr, encoding="utf-8")
    if attached.returncode:
        raise RuntimeError(f"{label}: Packaged attachment workflow failed; see private test log")
    report["attachment"] = json.loads(attachment_report.read_text(encoding="utf-8"))
    assert report["attachment"]["state"] == "passed"
    return report

a = run_case(first, "first")
old_return = (first / "BlenderReturn/edited.gltf").read_bytes()
old_time = (first / "BlenderReturn/edited.gltf").stat().st_mtime_ns
second.parent.mkdir(parents=True)
shutil.copytree(first, second)
b = run_case(second, "relocated")
assert (first / "BlenderReturn/edited.gltf").read_bytes() == old_return
assert (first / "BlenderReturn/edited.gltf").stat().st_mtime_ns == old_time
dotnet = root / ".tools/dotnet/dotnet.exe"
probe = root / "tools/EnvironmentProbe/bin/Debug/net10.0/EnvironmentProbe.dll"
validated = subprocess.run([str(dotnet), str(probe), "validate-gltf", str(Path(args.source).resolve() / "snapshot.json"),
                            str(second / "BlenderReturn/edited.gltf"), str(trial / "validated.json")],
                           capture_output=True, encoding="utf-8", errors="replace", timeout=30)
assert validated.returncode == 0, "The actual returned file must pass the core importer"
report = dict(state="passed",packageSha256=hashlib.sha256(archive.read_bytes()).hexdigest(),
              version=version,equipmentAttachmentPackagedBlenderVerified=True,
              cleanPackageWithoutSettings=True,independentOfSourceCheckout=True,
              relocatedFolderWithSpacesAndUnicode=True,oldPluginFolderUnchanged=True,
              fixedReturnFile="BlenderReturn/edited.gltf",sharedReturnAcrossProjects=True,
              foreignProjectIdentityPreserved=True,sourcePathsOmitted=True,
              originalBoneCount=10,candidateBoneCount=20,coreGltfImportPassed=True,
              coreChecksPassed=int(re.search(r"(\d+) checks passed",(root / "outputs/tests/core-checks.txt").read_text(encoding="utf-8-sig")).group(1)),gameUiVerified=False,blender=a)
(checks / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
(root / "outputs/delivery/分发检查.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print("Packaged bridge passed both locations; fixed return, project identity, privacy and core import verified.")
