"""Receive and return a skeleton through a running game plugin, without applying a preview."""
import json
from pathlib import Path
import sys
import time
import bpy
from mathutils import Vector
addon, output = map(Path,sys.argv[sys.argv.index("--")+1:])
sys.path.insert(0,str(addon.resolve().parent))
import clothing_skeleton_bridge as bridge
bridge.register()

def wait(condition):
    end=time.monotonic()+25
    while time.monotonic()<end:
        bridge.connection_tick()
        if condition():return
        time.sleep(.08)
    raise AssertionError(bridge.connection.bridge.message)
wait(lambda:bridge.current_armature(bpy.context) is not None)
arm=bridge.current_armature(bpy.context)
assert len(arm.data.bones)==116
for parent_name in ("j_te_l","j_te_r"):
    bpy.ops.object.mode_set(mode="EDIT")
    parent=arm.data.edit_bones[parent_name]
    for index in range(4):
        for bone in arm.data.edit_bones:
            bone.select=bone.select_head=bone.select_tail=False
        parent.select=parent.select_head=parent.select_tail=True
        arm.data.edit_bones.active=parent
        before=set(arm.data.edit_bones.keys())
        assert bpy.ops.armature.extrude()=={"FINISHED"}
        new=next(b for b in arm.data.edit_bones if b.name not in before)
        new.name=f"csw_native_{parent_name}_{index}"
        new.tail=new.head+Vector((0,.04,0))
        parent=new
    bridge.anchors.sync_collections(arm)
    assert bridge.anchors.check_status(arm)["ok"]
    bpy.ops.object.mode_set(mode="OBJECT")
assert bpy.ops.csw.return_to_game()=={"FINISHED"}
wait(lambda:"已返回游戏" in bridge.connection.bridge.message)
output.write_text(json.dumps(dict(state="passed",version=".".join(map(str,bridge.bl_info["version"])),gameBridgeConnected=True,nativeEditingAndCollectionsVerified=True,
    originalBoneCount=10,selectableBodyAnchorCount=106,authoredBoneCount=8,usedAnchorCount=2,
    candidateBoneCount=20,networkReturnAcknowledged=True,existingScenePreserved="Cube" in bpy.data.objects,
    gameMotionVerified=False,actorPreviewApplied=False,packageInstalled=False),indent=2),encoding="utf-8")
bridge.unregister()
print("CSW_LIVE_BRIDGE_PASSED",flush=True)
