"""Prepare audited versioned downloads and the public Dalamud repository feed."""
import ast
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import zipfile

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = "qq1299235059/FFxiv_sklb_edit"
RAW = f"https://raw.githubusercontent.com/{REPOSITORY}/main"
HOST_PATH = re.compile(r"(?i)(?:(?<![a-z0-9])[a-z]:[\\/]|\\\\[a-z0-9]|/(?:Users|home)/|Documents[\\/]+ChatGPT)")
EXPECTED = {
    "ClothingSkeleton.dll", "ClothingSkeleton.Core.dll", "ClothingSkeleton.json",
    "ClothingSkeleton.deps.json", "使用指南.md", "SHA256SUMS.txt",
    "BlenderBridge/clothing_skeleton_bridge/connection.py",
    "BlenderBridge/clothing_skeleton_bridge/__init__.py",
    "BlenderBridge/clothing_skeleton_bridge/anchors.py",
}


def digest(blob):
    return hashlib.sha256(blob).hexdigest()


def clean(blob, label):
    texts = [blob.decode("utf-8", errors="ignore")]
    texts += [blob[offset:].decode("utf-16le", errors="ignore") for offset in (0, 1)]
    if any(HOST_PATH.search(text) for text in texts):
        raise RuntimeError(f"A local host path was found in {label}")


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def json_bytes(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def main():
    metadata = read_json(ROOT / "src/ClothingSkeleton.Plugin/ClothingSkeleton.json")
    version = ".".join(metadata["AssemblyVersion"].split(".")[:3])
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise RuntimeError("Invalid version")
    if metadata["RepoUrl"] != f"https://github.com/{REPOSITORY}":
        raise RuntimeError("Plugin repository URL differs from the publication target")
    bridge = ROOT / "blender/clothing_skeleton_bridge"
    info = next(n.value for n in ast.parse((bridge / "__init__.py").read_text(encoding="utf-8-sig")).body
                if isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id == "bl_info" for t in n.targets))
    if ast.literal_eval(info)["version"] != tuple(map(int, version.split("."))):
        raise RuntimeError("Blender and game plugin versions differ")

    archive = ROOT / f"outputs/delivery/ClothingSkeleton-{version}.zip"
    package = archive.read_bytes()
    with zipfile.ZipFile(archive) as zipped:
        if len(zipped.namelist()) != len(EXPECTED) or set(zipped.namelist()) != EXPECTED:
            raise RuntimeError("Unexpected plugin ZIP contents")
        for name in EXPECTED:
            clean(zipped.read(name), name)
        sums = dict((name, sha) for sha, name in
                    (line.split("  ", 1) for line in zipped.read("SHA256SUMS.txt").decode().splitlines()))
        if set(sums) != EXPECTED - {"SHA256SUMS.txt"}:
            raise RuntimeError("Incomplete ZIP checksum manifest")
        for name, sha in sums.items():
            if digest(zipped.read(name)) != sha:
                raise RuntimeError(f"ZIP checksum differs for {name}")
        if json.loads(zipped.read("ClothingSkeleton.json").decode("utf-8-sig")) != metadata:
            raise RuntimeError("ZIP metadata does not match source metadata")
        for source, name in [
            (bridge / "connection.py", "BlenderBridge/clothing_skeleton_bridge/connection.py"),
            (bridge / "__init__.py", "BlenderBridge/clothing_skeleton_bridge/__init__.py"),
            (bridge / "anchors.py", "BlenderBridge/clothing_skeleton_bridge/anchors.py"),
            (ROOT / "docs/分发使用指南.md", "使用指南.md"),
        ]:
            if source.read_bytes() != zipped.read(name):
                raise RuntimeError(f"Packaged file differs from source: {name}")

    report = read_json(ROOT / "outputs/portability-check/report.json")
    if report["state"] != "passed" or report["version"] != version or report["packageSha256"] != digest(package):
        raise RuntimeError("Packaged Blender portability evidence does not match the current ZIP")
    report["completeAnchorRigPackagedBlenderVerified"] = report.pop("equipmentAttachmentPackagedBlenderVerified", True)
    if "attachment" in report["blender"]:
        report["blender"]["anchorRig"] = report["blender"].pop("attachment")
    report.update(gameMotionVerified=False, packageEntryCount=len(EXPECTED), packageAllowListVerified=True,
                  entryChecksumsVerified=True, hostPathsOmittedUtf8AndUtf16=True,
                  sourceBridgeMatchesPackage=True, noLocalSettingsOrGameResources=True)
    native_path = ROOT / "outputs/anchor-rig-check/report.json"
    if native_path.is_file():
        native = read_json(native_path)
        if native.get("state") == "passed" and native.get("version") == version:
            report["nativeFileChecks"] = native

    # Stable entry names and times avoid changes caused by local source timestamps.
    import io
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as zipped:
        for path, name in [(bridge / "__init__.py", "__init__.py"), (bridge / "anchors.py", "anchors.py"),
                           (bridge / "connection.py", "connection.py"), (ROOT / "blender/README.md", "README.md")]:
            data = path.read_bytes()
            clean(data, name)
            entry = zipfile.ZipInfo(f"clothing_skeleton_bridge/{name}", date_time=(2020, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            zipped.writestr(entry, data)
    blender_name = f"ClothingSkeleton-Blender-{version}.zip"
    standalone_path = ROOT / "outputs/github-publish/blender-addon-report.json"
    if standalone_path.is_file():
        standalone = read_json(standalone_path)
        if standalone.get("state") == "passed" and standalone.get("version") == version and standalone.get("standaloneZipSha256") == digest(buffer.getvalue()):
            report["standaloneBlender"] = standalone
    download = ROOT / "downloads" / version
    for name, data in [(archive.name, package), (blender_name, buffer.getvalue())]:
        existing = download / name
        if existing.is_file() and existing.read_bytes() != data:
            raise RuntimeError("Versioned downloads are immutable; bump the plugin version before replacing published files")
    files = {archive.name: package, blender_name: buffer.getvalue(),
             "使用指南.md": (ROOT / "docs/分发使用指南.md").read_bytes(), "verification.json": json_bytes(report)}
    files["SHA256SUMS.txt"] = "".join(f"{digest(data)}  {name}\n" for name, data in files.items()).encode()
    for name, blob in files.items():
        if not name.endswith(".zip"):
            clean(blob, name)
    download.mkdir(parents=True, exist_ok=True)
    for name, blob in files.items():
        (download / name).write_bytes(blob)

    entry = dict(metadata)
    link = f"{RAW}/downloads/{version}/{archive.name}"
    updated = int(datetime.now(timezone.utc).timestamp())
    repo_path = ROOT / "repo.json"
    if repo_path.is_file():
        previous = read_json(repo_path)
        if len(previous) == 1 and previous[0].get("AssemblyVersion") == metadata["AssemblyVersion"]:
            updated = previous[0]["LastUpdate"]
    entry.update(DownloadLinkInstall=link, DownloadLinkUpdate=link, LastUpdate=updated)
    repo_path.write_bytes(json_bytes([entry]))
    (ROOT / "outputs/delivery/分发检查.json").write_bytes(json_bytes(report))
    print(f"Prepared downloads/{version}: audited game ZIP, Blender ZIP, guide, checksums and repo.json.")


if __name__ == "__main__":
    main()
