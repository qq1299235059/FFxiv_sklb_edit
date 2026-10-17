"""Package this workspace's verified development build and private local sample."""
import hashlib
import json
import re
import runpy
from pathlib import Path
import shutil
import sys
import zipfile

sys.stdout.reconfigure(encoding="utf-8")
root = Path(__file__).resolve().parents[1]
if "--private-test-artifacts" not in sys.argv:
    runpy.run_path(str(root / "tools/package_release.py"))
    raise SystemExit(0)
delivery = root / "outputs/private-history/local-delivery"
delivery.mkdir(parents=True, exist_ok=True)
reports = sorted((root / "outputs/e2e").glob("*/report.json"), reverse=True)
if not reports:
    raise RuntimeError("Complete tools/e2e.ps1 first")
report = json.loads(reports[0].read_text(encoding="utf-8-sig"))
if report["state"] != "passed" or not report["originalSqPackOnly"]:
    raise RuntimeError("Delivery requires a passed original-SqPack integration run")
project = Path(report["projectDirectory"])
candidate = Path(report["candidateDirectory"])
validation = json.loads((candidate / "validation.json").read_text(encoding="utf-8-sig"))
candidate_bytes = (candidate / "candidate.sklb").read_bytes()
candidate_hash = hashlib.sha256(candidate_bytes).hexdigest()
if validation["candidateSha256"] != candidate_hash:
    raise RuntimeError("Candidate has changed since validation")
receipt = json.loads((project / "source-receipt.json").read_text(encoding="utf-8-sig"))
if receipt["kind"] != "original-sqpack" or receipt["penumbraResolutionUsed"]:
    raise RuntimeError("Sample is not an original SqPack source")
if hashlib.sha256((project / "original.sklb").read_bytes()).hexdigest() != validation["sourceSha256"]:
    raise RuntimeError("Original sample changed")
unit_log = root / "outputs/tests/core-checks.txt"
unit_text = unit_log.read_text(encoding="utf-8-sig")
unit_result=re.search(r"(\d+) checks passed\.",unit_text)
if unit_result is None or "FAIL:" in unit_text:
    raise RuntimeError("Core check evidence is missing or failed")

plugin_dir = root / "src/ClothingSkeleton.Plugin/bin/Debug"
plugin_files = ["ClothingSkeleton.dll", "ClothingSkeleton.Core.dll", "ClothingSkeleton.json", "ClothingSkeleton.deps.json", "ClothingSkeleton.pdb", "ClothingSkeleton.Core.pdb"]
with zipfile.ZipFile(delivery / "ClothingSkeleton-Dalamud-0.1.0-dev.zip", "w", zipfile.ZIP_DEFLATED) as archive:
    for name in plugin_files:
        archive.write(plugin_dir / name, name)
with zipfile.ZipFile(delivery / "ClothingSkeleton-Blender-0.1.0.zip", "w", zipfile.ZIP_DEFLATED) as archive:
    archive.write(root / "blender/clothing_skeleton_bridge/__init__.py", "clothing_skeleton_bridge/__init__.py")

sample = delivery / "sample-project"
sample.mkdir(exist_ok=True)
for name in ["original.sklb", "snapshot.json", "source-receipt.json", "skeleton.gltf", "skeleton.ffxiv.json", "skeleton.complete.json", "reference.gltf", "reference.ffxiv.json", "reference.complete.json", "reference-snapshot.json"]:
    shutil.copy2(project / name, sample / name)
(sample / "candidate").mkdir(exist_ok=True)
for name in ["candidate.sklb", "validation.json", "validated-exchange.json", "candidate-snapshot.json", "model-check.json"]:
    shutil.copy2(candidate / name, sample / "candidate" / name)
(sample / "blender").mkdir(exist_ok=True)
for path in Path(report["blenderDirectory"]).iterdir():
    if path.is_file() and path.suffix in {".blend", ".gltf", ".bin", ".json"}:
        shutil.copy2(path, sample / "blender" / path.name)
chain_blend=sample / "blender/clothing-chain-test.blend"
if chain_blend.is_file():shutil.copy2(chain_blend,sample / "blender/clothing-test.blend")

packages=[Path(report["estPackage"])] if report.get("estPackage") else sorted((root / "outputs/mods").glob("*.pmp"),reverse=True)
for path in packages:
    with zipfile.ZipFile(path) as archive:
        if archive.read("files/skeleton.sklb") == candidate_bytes:
            shutil.copy2(path, delivery / "ԭ��¶������Ǽܲ���.pmp")
            if report.get("estPackage"):
                shutil.copy2(path, delivery / "��ԭŮ�¶�����_EST9100.pmp")
            break
else:
    raise RuntimeError("No matching tested PMP exists")
for name in ["����ָ��.md", "��������.md", "�����ο�.md"]:
    shutil.copy2(root / "docs" / name, delivery / name)
shutil.copy2(unit_log, delivery / "���ļ���¼.txt")
report.update(
    coreChecksPassed=int(unit_result.group(1)),
    sourceSha256=validation["sourceSha256"],
    candidateSha256=candidate_hash,
    sourceReceipt=receipt,
    modelCheck=json.loads((candidate / "model-check.json").read_text(encoding="utf-8-sig")),
    blenderCheck=json.loads((Path(report["blenderDirectory"]) / "blender-report.json").read_text(encoding="utf-8-sig")),
    pluginSha256=hashlib.sha256((plugin_dir / "ClothingSkeleton.dll").read_bytes()).hexdigest(),
    sampleDirectory=str(sample),
    samplePurpose="Local private original-equipment skeleton conversion and append test; no weighted garment or motion verification",
)
(delivery / "���Ա���.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
if report.get("estPackage"):
    for name in ["est-assignment.json","deployment-before.json","deployment.json","metadata-effect-test.json","metadata-editor.png"]:
        source=root / "outputs/female-est-test" / name
        if source.is_file():shutil.copy2(source,delivery / name)
if report.get("runtimeActorBonesVerified"):
    for name in ["runtime-bones.json","ktisis-bone-list.png","ktisis-selected-chain-root.png","vfx-layers.png"]:
        source=root / "outputs/chain-test" / name
        if source.is_file():shutil.copy2(source,delivery / name)
workflow_report=root / "outputs/workflow-test/�����̼��.json"
if workflow_report.is_file():
    workflow=json.loads(workflow_report.read_text(encoding="utf-8-sig"))
    if workflow["state"]!="passed" or workflow["pluginSha256"]!=report["pluginSha256"]:
        raise RuntimeError("Simplified workflow evidence does not match the packaged plugin")
    if workflow["blender"]["bridgeSha256"]!=hashlib.sha256((root / "blender/clothing_skeleton_bridge/__init__.py").read_bytes()).hexdigest():
        raise RuntimeError("Blender bridge changed after workflow verification")
    if workflow["blender"]["launcherSha256"]!=hashlib.sha256((root / "tools/blender_open_project.py").read_bytes()).hexdigest():
        raise RuntimeError("Blender launcher changed after workflow verification")
    shutil.copy2(workflow_report,delivery / "�����̼��.json")
    report["simplifiedWorkflow"]=workflow
    (delivery / "���Ա���.json").write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding="utf-8")
manifest = {str(p.relative_to(delivery)):hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(delivery.rglob("*")) if p.is_file() and p.name != "SHA256.json"}
(delivery / "SHA256.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
print(f"Delivery ready: {delivery}")
