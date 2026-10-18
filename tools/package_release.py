"""Create an allow-listed, portable plugin ZIP without any local runtime state."""
import hashlib
import json
from pathlib import Path
import re
import shutil
import sys
import zipfile

root = Path(__file__).resolve().parents[1]
build = root / "src/ClothingSkeleton.Plugin/bin/Release"
destination = root / "outputs/release"
destination.mkdir(parents=True, exist_ok=True)
files = ["ClothingSkeleton.dll", "ClothingSkeleton.Core.dll", "ClothingSkeleton.json",
         "ClothingSkeleton.deps.json", "使用指南.md", "BlenderBridge/clothing_skeleton_bridge/connection.py",
         "BlenderBridge/clothing_skeleton_bridge/__init__.py", "BlenderBridge/clothing_skeleton_bridge/anchors.py", "BlenderBridge/clothing_skeleton_bridge/assets.py"]
private = re.compile(r"(?i)(?:(?<![a-z0-9])[a-z]:[\\/]|\\\\[a-z0-9]|/(?:Users|home)/|WorkspaceRoot|Documents[\\/]+ChatGPT)")
entries = {}
for name in files:
    data = (build / name).read_bytes()
    candidates = [data.decode("utf-8", errors="ignore")]
    candidates += [data[offset:].decode("utf-16le", errors="ignore") for offset in (0, 1)]
    if any(private.search(text) for text in candidates):
        raise RuntimeError(f"An absolute host path or build-checkout reference was found in {name}")
    entries[name] = data
manifest = "".join(f"{hashlib.sha256(data).hexdigest()}  {name}\n" for name, data in entries.items())
entries["SHA256SUMS.txt"] = manifest.encode("utf-8")
version = ".".join(json.loads((build / "ClothingSkeleton.json").read_text(encoding="utf-8-sig"))["AssemblyVersion"].split(".")[:3])
if not re.fullmatch(r"\d+\.\d+\.\d+", version):
    raise RuntimeError("Invalid plugin package version")
archive = destination / f"ClothingSkeleton-{version}.zip"
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as output:
    for name, data in entries.items():
        output.writestr(name, data)
delivery = root / "outputs/delivery"
delivery.mkdir(parents=True, exist_ok=True)
allowed = {archive.name, "使用指南.md", "分发检查.json", "SHA256SUMS.txt"}
if any(path.name not in allowed for path in delivery.iterdir()):
    raise RuntimeError("Private or unexpected files exist in outputs/delivery; preserve them under private-history before distribution")
shutil.copy2(archive, delivery / archive.name)
shutil.copy2(build / "使用指南.md", delivery / "使用指南.md")
checksum = hashlib.sha256(archive.read_bytes()).hexdigest()
(delivery / "SHA256SUMS.txt").write_text(f"{checksum}  {archive.name}\n", encoding="utf-8")
print(f"Created outputs/release/ClothingSkeleton-{version}.zip; clean allow-list and path scan passed.")
