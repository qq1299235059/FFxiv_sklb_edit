"""Run the actual loopback host and Blender add-on without a game or user Blender window."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import uuid

parser=argparse.ArgumentParser()
parser.add_argument("--blender",required=True)
parser.add_argument("--source",required=True)
parser.add_argument("--addon",required=True)
parser.add_argument("--output",required=True)
parser.add_argument("--return-root")
a=parser.parse_args()
root=Path(__file__).resolve().parents[1]
out=Path(a.output).resolve();out.mkdir(parents=True,exist_ok=True)
trial=Path(tempfile.mkdtemp(prefix="network-",dir=out))
source=Path(a.source).resolve()
second=trial/"source two";second.mkdir()
# Clone an independent project identity while retaining original provenance and bones.
new_id=str(uuid.uuid4())
for name in ("snapshot.json","anchor-rig.json","skeleton.gltf","skeleton.ffxiv.json","skeleton.complete.json","reference.gltf","reference.ffxiv.json","reference.complete.json","skeleton.bin","reference.bin"):
 p=source/name
 if not p.exists():continue
 if name in {"snapshot.json","anchor-rig.json","skeleton.ffxiv.json","skeleton.complete.json"}:
  data=json.loads(p.read_text(encoding="utf-8-sig"));data["projectId"]=new_id
  (second/name).write_text(json.dumps(data),encoding="utf-8")
 elif name=="skeleton.gltf":
  data=json.loads(p.read_text(encoding="utf-8-sig"));data["extras"]["cswProjectId"]=new_id
  (second/name).write_text(json.dumps(data),encoding="utf-8")
 else:shutil.copy2(p,second/name)
complete=json.loads((second/"skeleton.complete.json").read_text());complete["gltfSha256"]=hashlib.sha256((second/"skeleton.gltf").read_bytes()).hexdigest()
(second/"skeleton.complete.json").write_text(json.dumps(complete),encoding="utf-8")
dotnet=root/".tools/dotnet/dotnet.exe";probe=root/"tools/EnvironmentProbe/bin/Debug/net10.0/EnvironmentProbe.dll"
ready=trial/"ready.json";control=trial/"control";returned=(Path(a.return_root).resolve() if a.return_root else trial/"plugin with spaces")/"BlenderReturn/edited.gltf"
host_log=(trial/"host.log").open("w",encoding="utf-8")
host=subprocess.Popen([str(dotnet),str(probe),"bridge-host",str(source),str(returned),str(ready),str(control)],stdout=host_log,stderr=subprocess.STDOUT)
blender=None

def wait(predicate,timeout=40):
 end=time.monotonic()+timeout
 while time.monotonic()<end:
  if predicate():return
  if host.poll() is not None:raise RuntimeError("Transport host exited; inspect private test log")
  if blender and blender.poll() is not None:raise RuntimeError("Blender exited before test completed; inspect private test log")
  time.sleep(.1)
 raise RuntimeError("Network test timed out; inspect private test log")

try:
 wait(ready.exists)
 port=json.loads(ready.read_text())["port"]
 env=os.environ.copy();env["BLENDER_USER_RESOURCES"]=str(trial/"Blender resources");env["TEMP"]=str(trial);env["TMP"]=str(trial)
 blog=(trial/"blender.log").open("w",encoding="utf-8")
 blender=subprocess.Popen([a.blender,"--background","--factory-startup","--python-exit-code","1","--python",str(root/"tools/blender_network_smoke.py"),"--",str(Path(a.addon).resolve()),str(port),str(trial/"client")],stdout=blog,stderr=subprocess.STDOUT,env=env)
 client=trial/"client"
 wait(lambda:(client/"first-return").exists())
 first=returned.read_bytes();assert len(json.loads(first)["skins"][0]["joints"])==20
 core=subprocess.run([str(dotnet),str(probe),"validate-gltf",str(source/"snapshot.json"),str(returned),str(trial/"first-validated.json")],capture_output=True,timeout=20)
 assert core.returncode==0,core.stderr
 shutil.copytree(returned.parent,trial/"first-return")
 control.write_text(str(source),encoding="utf-8");(client/"resend-ready").touch()
 wait(lambda:(client/"ready-second").exists())
 control.write_text(str(second),encoding="utf-8");(client/"second-published").touch()
 wait(lambda:(client/"blender-report.json").exists())
 assert blender.wait(timeout=20)==0
 second_return=json.loads(returned.read_text(encoding="utf-8"));assert len(second_return["skins"][0]["joints"])==15
 core=subprocess.run([str(dotnet),str(probe),"validate-gltf",str(second/"snapshot.json"),str(returned),str(trial/"second-validated.json")],capture_output=True,timeout=20)
 assert core.returncode==0,core.stderr
 report=json.loads((client/"blender-report.json").read_text());report.update(coreGltfImportPassed=True,loopbackOnly=True,firstReturnBoneCount=20,secondReturnBoneCount=15)
 (out/"report.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
 (out/"latest-trial.txt").write_text(str(trial),encoding="utf-8")
 print("Network round trip passed in one Blender process; preserved edits and both returns validated.")
finally:
 (Path(str(control)+".stop")).touch()
 if blender and blender.poll() is None:blender.terminate();blender.wait(timeout=10)
 if host.poll() is None:host.wait(timeout=10)
 host_log.close()
