"""Exercise the same launcher and return operator used by the simplified UI."""
import hashlib
import json
from pathlib import Path
import runpy
import shutil
import uuid
import sys

import bpy

root = Path(__file__).resolve().parents[1]
arguments = sys.argv[sys.argv.index("--") + 1:]
source, output = (Path(arg).resolve() for arg in arguments[:2])
plugin = Path(arguments[2]).resolve()
project = plugin / "Projects" / "workflow-a"
project.mkdir(parents=True, exist_ok=True)
output.mkdir(parents=True, exist_ok=True)
for path in source.iterdir():
    if path.name == "anchor-rig.json" or path.name.startswith(("skeleton.", "reference.")) and path.suffix in {".gltf", ".bin", ".json"}:
        shutil.copy2(path, project / path.name)
(project / "bridge.json").write_text(json.dumps(dict(schemaVersion=1, pluginRoot="../..")), encoding="utf-8")
sys.argv = [sys.argv[0], "--", str(project / "skeleton.gltf")]
runpy.run_path(str(plugin / "BlenderBridge/blender_open_project.py"))
bridge = sys.modules["clothing_skeleton_bridge"]
armature = bridge.current_armature(bpy.context)
assert armature and len(armature.data.bones) == 116
assert bpy.ops.csw.add_chain() == {"FINISHED"}
assert armature.mode == "EDIT"
assert bpy.ops.csw.return_to_game() == {"FINISHED"}
returned = plugin / "BlenderReturn/edited.gltf"
assert not (project / "return").exists()
gltf = json.loads(returned.read_text(encoding="utf-8"))
manifest = json.loads(returned.with_suffix(".complete.json").read_text(encoding="utf-8"))
assert manifest["boneCount"] == 14
assert manifest["gltfSha256"] == hashlib.sha256(returned.read_bytes()).hexdigest()
assert len(gltf["skins"]) == 1 and len(gltf["skins"][0]["joints"]) == 14
assert not any("�����Ǽ�" in node.get("name", "") for node in gltf["nodes"])
assert len({node["extras"]["cswBoneId"] for node in gltf["nodes"] if "cswBoneId" in node.get("extras", {})}) == 14
# All exported JSON must omit Blender-only source paths.
assert "csw_source_file" not in returned.read_text(encoding="utf-8")
assert "csw_return_file" not in returned.read_text(encoding="utf-8")
assert str(plugin) not in returned.read_text(encoding="utf-8")
# Returning still works when a different object is selected in the scene.
bpy.ops.object.select_all(action="DESELECT")
bpy.context.view_layer.objects.active = None
assert bridge.current_armature(bpy.context) == armature
assert bpy.ops.csw.return_to_game() == {"FINISHED"}
# A second project uses the same destination while keeping a different identity.
second = plugin / "Projects/workflow-b"
second.mkdir(parents=True, exist_ok=True)
for path in project.iterdir():
    if path.is_file():
        shutil.copy2(path, second / path.name)
second_id = str(uuid.uuid4())
second_gltf = json.loads((second / "skeleton.gltf").read_text(encoding="utf-8"))
second_gltf["extras"]["cswProjectId"] = second_id
(second / "skeleton.gltf").write_text(json.dumps(second_gltf), encoding="utf-8")
second_sidecar = json.loads((second / "skeleton.ffxiv.json").read_text(encoding="utf-8-sig"))
second_sidecar["projectId"] = second_id
(second / "skeleton.ffxiv.json").write_text(json.dumps(second_sidecar), encoding="utf-8")
if (second / "anchor-rig.json").is_file():
    descriptor = json.loads((second / "anchor-rig.json").read_text(encoding="utf-8-sig"))
    descriptor["projectId"] = second_id
    (second / "anchor-rig.json").write_text(json.dumps(descriptor), encoding="utf-8")
second_armature = bridge.import_project(second / "skeleton.gltf")
assert bpy.ops.csw.add_chain() == {"FINISHED"}
assert bpy.ops.csw.return_to_game() == {"FINISHED"}
assert bridge.return_path(second_armature) == returned
assert json.loads(returned.with_suffix(".complete.json").read_text(encoding="utf-8"))["projectId"] == second_id
# Restore the first project's output for the core importer to validate.
bridge.export_project(armature)
assert json.loads(returned.with_suffix(".complete.json").read_text(encoding="utf-8"))["projectId"] == manifest["projectId"]
before = returned.read_bytes()
bpy.context.view_layer.objects.active = armature
armature.select_set(True)
bpy.ops.object.mode_set(mode="EDIT")
armature.data.edit_bones[0].head.x += 0.1
try:
    result = bpy.ops.csw.return_to_game()
except RuntimeError:
    result = {"CANCELLED"}
assert result == {"CANCELLED"}
assert returned.read_bytes() == before, "A rejected edit must not replace the valid return file"
bpy.ops.object.mode_set(mode="OBJECT")
report = dict(state="passed",launcherImportedProject=True,sessionBridgeRegistered=True,
              fixedReturnPath="BlenderReturn/edited.gltf",sharedReturnAcrossProjects=True,foreignProjectIdentityPreserved=True,sourcePathsOmitted=True,originalBoneCount=10,candidateBoneCount=14,
              chainOperatorEnteredEditMode=True,returnWithoutArmatureSelection=True,
              originalEditRejected=True,validReturnPreservedAfterFailure=True,
              referenceExcluded=True,completionHashVerified=True,
              bridgeSha256=hashlib.sha256((plugin / "BlenderBridge/clothing_skeleton_bridge/__init__.py").read_bytes()).hexdigest(),
              launcherSha256=hashlib.sha256((plugin / "BlenderBridge/blender_open_project.py").read_bytes()).hexdigest())
(output / "workflow-report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print("CSW_WORKFLOW_PASSED", flush=True)
