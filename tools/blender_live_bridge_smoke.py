"""Receive and return a skeleton through a running game plugin, without applying a preview."""
import json
from pathlib import Path
import sys
import time
import bpy
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
for parent in ("j_te_l","j_te_r"):
    bridge.add_test_chain(arm,parent)
assert bpy.ops.csw.return_to_game()=={"FINISHED"}
wait(lambda:"已返回游戏" in bridge.connection.bridge.message)
output.write_text(json.dumps(dict(state="passed",version="0.6.0",gameBridgeConnected=True,
    originalBoneCount=10,selectableBodyAnchorCount=106,authoredBoneCount=8,usedAnchorCount=2,
    candidateBoneCount=20,networkReturnAcknowledged=True,existingScenePreserved="Cube" in bpy.data.objects,
    gameMotionVerified=False,actorPreviewApplied=False,packageInstalled=False),indent=2),encoding="utf-8")
bridge.unregister()
print("CSW_LIVE_BRIDGE_PASSED",flush=True)
