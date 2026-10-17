"""Fetch a pinned Microsoft SDK into this project, checking Microsoft's SHA512."""
import hashlib
import json
from pathlib import Path
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = "10.0.100"
metadata_url = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json"
with urllib.request.urlopen(metadata_url, timeout=30) as response:
    metadata = json.load(response)
matches = []
for release in metadata["releases"]:
    for sdk in release.get("sdks", [release.get("sdk", {})]):
        if sdk.get("version") == VERSION:
            matches.extend(f for f in sdk["files"] if f["rid"] == "win-x64" and f["name"].endswith(".zip"))
if not matches:
    raise RuntimeError(f"Microsoft metadata does not contain SDK {VERSION} win-x64 zip")
asset = matches[0]
archive = ROOT / ".tools" / f"dotnet-sdk-{VERSION}.zip"
archive.parent.mkdir(parents=True, exist_ok=True)
digest = hashlib.sha512()
print(f"Downloading Microsoft .NET SDK {VERSION}", flush=True)
with urllib.request.urlopen(asset["url"], timeout=60) as response, archive.open("wb") as destination:
    while chunk := response.read(1024 * 1024):
        digest.update(chunk)
        destination.write(chunk)
if digest.hexdigest().lower() != asset["hash"].lower():
    raise RuntimeError("Microsoft SDK SHA512 verification failed")
destination = ROOT / ".tools" / "dotnet"
destination.mkdir(exist_ok=True)
with zipfile.ZipFile(archive) as sdk_zip:
    for item in sdk_zip.infolist():
        if not (destination / item.filename).resolve().is_relative_to(destination.resolve()):
            raise RuntimeError("SDK archive contains an invalid path")
    sdk_zip.extractall(destination)
print(f"Verified SDK extracted to {destination}", flush=True)

