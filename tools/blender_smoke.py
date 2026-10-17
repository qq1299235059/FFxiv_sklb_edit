"""Run with Blender --background --factory-startup --python ... -- INPUT OUTPUT_DIR."""
import json
from pathlib import Path
import sys

root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(root / "blender"))
import clothing_skeleton_bridge as bridge
import bpy

arguments = sys.argv[sys.argv.index("--")+1:]
input_path = Path(arguments[0]).resolve()
output = Path(arguments[1]).resolve()
output.mkdir(parents=True, exist_ok=True)
bridge.register()
armature = bridge.import_project(input_path)
references = [obj for obj in bpy.data.objects if obj.get("csw_reference") and obj.type == "ARMATURE"]
assert len(references) == 1, "Original base reference was not imported separately"
assert references[0].hide_select, "Reference must not be selected for editing"
bridge.export_project(armature, output / "untouched.gltf")
created = bridge.add_test_bone(armature, "csw_test_ribbon")
bridge.export_project(armature, output / "appended.gltf")
original = armature.data.bones[0]
original_name = original.name
original.name = "invalid_original_rename"
try:
    bridge.export_project(armature, output / "must-not-export.gltf")
except ValueError as error:
    assert "renamed" in str(error)
else:
    raise AssertionError("Original bone rename was not rejected")
finally:
    original.name = original_name
bpy.ops.wm.save_as_mainfile(filepath=str(output / "clothing-test.blend"))
(output / "blender-report.json").write_text(json.dumps({"blenderVersion":bpy.app.version_string,"originalBoneCount":len(armature.data.bones)-1,"appendedBoneCount":len(armature.data.bones),"newBone":created,"baseReferenceImportedSeparately":True,"originalRenameRejected":True,"state":"exported"},indent=2),encoding="utf-8")
print("CSW_BLENDER_SMOKE_EXPORTED", flush=True)
