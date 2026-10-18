"""Send a bounded local development request to the already-loaded plugin."""
import argparse
import json
from pathlib import Path
import time
import sys

sys.stdout.reconfigure(encoding="utf-8")

parser = argparse.ArgumentParser()
parser.add_argument("action", choices=("select-character", "inspect", "append", "model-check", "penumbra-status", "preview-selftest", "package", "est-package", "install-est", "enable-est", "verify-est", "est-effect-test", "reload-est", "runtime-bones", "runtime-preview", "stop-preview", "show-ui", "workflow-status"))
parser.add_argument("--path")
parser.add_argument("--selection",choices=("self","target"),default="self")
parser.add_argument("--plugin-directory",type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
plugin = (args.plugin_directory or root / "src/ClothingSkeleton.Plugin/bin/Debug").resolve()
settings = plugin / "workshop-settings.json"
if not settings.is_file() or not json.loads(settings.read_text(encoding="utf-8-sig")).get("EnableDevelopmentCommands",False):
    raise RuntimeError("EnableDevelopmentCommands must be enabled explicitly in the installed plugin's local settings for development requests.")
expected = {"select-character":"character-selected", "inspect":"roundtrip-passed", "append":"append-passed", "model-check":"model-bones-passed", "penumbra-status":"penumbra-ready", "preview-selftest":"preview-selftest-passed", "package":"package-exported", "est-package":"est-package-exported", "install-est":"est-install-queued", "enable-est":"est-enabled", "verify-est":"est-verified", "est-effect-test":"est-effect-passed", "reload-est":"est-reloaded", "runtime-bones":"runtime-bones-captured", "runtime-preview":"runtime-preview-applied", "stop-preview":"preview-stopped", "show-ui":"ui-opened", "workflow-status":"workflow-ready"}[args.action]
request_file = plugin / "Development" / "runtime-command.json"
request_file.parent.mkdir(exist_ok=True)
started = time.time()
request = {"action":args.action}
if args.action == "select-character":
    request["selection"] = args.selection
if args.path:
    request["path"] = args.path
temporary = request_file.with_suffix(".tmp")
temporary.write_text(json.dumps(request), encoding="utf-8")
temporary.replace(request_file)
status_file = plugin / "Projects" / "status.json"
while time.time() - started < 25:
    if status_file.exists() and status_file.stat().st_mtime >= started:
        try:
            status = json.loads(status_file.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            time.sleep(.25)
            continue
        if status["state"] == expected:
            print(json.dumps(status, ensure_ascii=False, indent=2))
            break
        if status["state"] in ("failed", "command-failed", "model-bones-failed"):
            raise RuntimeError(status["detail"])
    time.sleep(.25)
else:
    raise TimeoutError("The loaded plugin did not complete this request within 25 seconds. Check /csw and Dalamud dev plugin state.")

