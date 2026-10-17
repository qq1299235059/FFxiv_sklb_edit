"""Blender background integration: original clothing skeleton -> three-segment chain."""
import json
from pathlib import Path
import sys

root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(root / "blender"))
import bpy
import clothing_skeleton_bridge as bridge

arguments = sys.argv[sys.argv.index("--") + 1:]
source = Path(arguments[0]).resolve()
output = Path(arguments[1]).resolve()
output.mkdir(parents=True, exist_ok=True)
for obj in list(bpy.data.objects):
    bpy.data.objects.remove(obj, do_unlink=True)
bridge.register()
armature = bridge.import_project(source)
original_count = len(armature.data.bones)
names = bridge.add_test_chain(armature, "n_ex_top")
positions = [armature.data.bones[name].head_local.copy() for name in names]
lengths = [(positions[i + 1] - positions[i]).length for i in range(3)]
assert all(length > 0.1 for length in lengths), "The visible chain must not collapse to zero length"
for index in range(1, 4):
    assert armature.data.bones[names[index]].parent.name == names[index - 1]
bridge.export_project(armature, output / "appended.gltf")
for bone in armature.data.bones:
    bone.select = bone.name in names
armature.data.bones.active = armature.data.bones[names[0]]
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type == "VIEW_3D":
            area.spaces.active.region_3d.view_location = positions[1]
            area.spaces.active.region_3d.view_distance = 1.3
bpy.ops.wm.save_as_mainfile(filepath=str(output / "clothing-chain-test.blend"))
report = {"state":"exported", "blenderVersion":bpy.app.version_string,
          "originalBoneCount":original_count, "appendedBoneCount":len(armature.data.bones),
          "newBones":names, "anchor":"n_ex_top", "visibleSegments":3,
          "segmentLengths":lengths, "jointPositions":[list(p) for p in positions],
          "terminalJointIncluded":True, "baseReferenceImportedSeparately":True}
(output / "blender-report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print("CSW_BLENDER_CHAIN_EXPORTED", flush=True)
