"""Install the standalone ZIP using Blender's own legacy add-on installer."""
import hashlib
import json
from pathlib import Path
import sys

import bpy
from mathutils import Vector

archive, project, plugin, report = [Path(p).resolve() for p in sys.argv[sys.argv.index("--") + 1:]]
assert bpy.ops.preferences.addon_install(filepath=str(archive), overwrite=True) == {"FINISHED"}
assert bpy.ops.preferences.addon_enable(module="clothing_skeleton_bridge") == {"FINISHED"}
bridge = sys.modules["clothing_skeleton_bridge"]
armature = bridge.import_project(project / "skeleton.gltf")
assert len(armature.data.bones) == 116
assert len([b for b in armature.data.bones if bridge.anchors.is_anchor(b)]) == 106
bpy.ops.object.mode_set(mode="EDIT")
for bone in armature.data.edit_bones:
    bone.select = False
armature.data.edit_bones["j_te_l"].select = True
armature.data.edit_bones.active = armature.data.edit_bones["j_te_l"]
before = set(armature.data.edit_bones.keys())
assert bpy.ops.armature.extrude() == {"FINISHED"}
created = next(b for b in armature.data.edit_bones if b.name not in before)
created.tail = created.head + Vector((0, .04, 0))
bridge.anchors.sync_collections(armature)
assert bpy.ops.csw.return_to_game() == {"FINISHED"}
returned = plugin / "BlenderReturn/edited.gltf"
assert bridge.return_path(armature) == returned
document = json.loads(returned.read_text(encoding="utf-8"))
assert len(document["skins"][0]["joints"]) == 12
assert [a["boneName"] for a in document["extras"]["cswAnchorBindings"]] == ["j_te_l"]
assert len(armature.data.bones) == 117
report.write_text(json.dumps(dict(state="passed", version=".".join(map(str, bridge.bl_info["version"])), standaloneZipSha256=hashlib.sha256(archive.read_bytes()).hexdigest(),
    installedWithBlenderAddonInstaller=True, addonEnabled=True, selectableBodyAnchorCount=106,
    authoredBoneCount=1, usedAnchorCount=1, candidateBoneCount=12, fixedReturnFile="BlenderReturn/edited.gltf",
    sourcePathsOmitted=str(plugin) not in returned.read_text(encoding="utf-8"), authoringScenePreserved=True,
    gameMotionVerified=False), indent=2), encoding="utf-8")
print("CSW_STANDALONE_ADDON_PASSED", flush=True)
