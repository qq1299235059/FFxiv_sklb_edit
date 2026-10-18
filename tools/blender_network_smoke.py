"""Exercise an installed add-on against a real C# loopback host in one Blender process."""
import json
import os
from pathlib import Path
import sys
import time
import bpy

addon, port, output = sys.argv[sys.argv.index("--") + 1:]
output = Path(output); output.mkdir(parents=True, exist_ok=True)
if addon.endswith(".zip"):
    assert bpy.ops.preferences.addon_install(filepath=str(Path(addon).resolve()), overwrite=True) == {"FINISHED"}
    assert bpy.ops.preferences.addon_enable(module="clothing_skeleton_bridge") == {"FINISHED"}
    bridge = sys.modules["clothing_skeleton_bridge"]
else:
    sys.path.insert(0, str(Path(addon).resolve().parent))
    import clothing_skeleton_bridge as bridge
    bridge.register()
bridge.connection.bridge.start(int(port), output / "cache")
sentinel = bpy.data.objects.get("Cube")
assert sentinel is not None
sentinel["unsaved_content"] = "keep me"
pid = os.getpid()

def wait(predicate, timeout=25):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        bridge.connection_tick()
        if predicate():
            return
        time.sleep(.08)
    raise AssertionError("Loopback stage timed out: " + bridge.connection.bridge.message)

def rigs():
    return [o for o in bpy.context.scene.objects if o.type == "ARMATURE" and "csw_network_revision" in o]

def signal(name):
    (output / name).write_text("ready", encoding="utf-8")

wait(lambda: len(rigs()) == 1)
arm = rigs()[0]
assert len(arm.data.bones) == 116
assert len([b for b in arm.data.bones if bridge.anchors.is_anchor(b)]) == 106
for name in ("j_te_l", "j_te_r"):
    bridge.add_test_chain(arm, name)
assert len(arm.data.bones) == 124
assert bpy.ops.csw.return_to_game() == {"FINISHED"}
wait(lambda: "已返回游戏" in bridge.connection.bridge.message)
signal("first-return")
wait(lambda: (output / "resend-ready").exists())
wait(lambda: "保留现有编辑" in bridge.connection.bridge.message)
assert len(rigs()) == 1 and len(arm.data.bones) == 124
assert sentinel["unsaved_content"] == "keep me"
# A new project arriving during Edit Mode must wait, never interrupt current editing.
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode="EDIT")
signal("ready-second")
wait(lambda: (output / "second-published").exists())
for _ in range(35):
    bridge.connection_tick(); time.sleep(.08)
assert len(rigs()) == 1 and arm.mode == "EDIT"
bpy.ops.object.mode_set(mode="OBJECT")
wait(lambda: len(rigs()) == 2)
new = next(o for o in rigs() if o != arm)
assert len(arm.data.bones) == 124 and len(new.data.bones) == 116
assert sentinel["unsaved_content"] == "keep me" and os.getpid() == pid
assert new["csw_source_file"] == "network"
new["csw_source_file"] = "missing-cache-marker"
bridge.add_test_chain(new, "j_te_l")
assert bpy.ops.csw.return_to_game() == {"FINISHED"}
wait(lambda: "已返回游戏" in bridge.connection.bridge.message)
# Disconnect/reconnect in the same session must keep authoring objects and edits.
bridge.connection.bridge.stop()
bridge.connection.bridge.start(int(port), output / "cache")
wait(lambda: "保留现有编辑" in bridge.connection.bridge.message)
assert len(rigs()) == 2 and len(new.data.bones) == 120
report = dict(state="passed", version=".".join(map(str, bridge.bl_info["version"])),
    sameBlenderProcess=True, existingUnsavedObjectsPreserved=True, noNewBlendProject=True,
    firstProjectCandidateBones=20, secondProjectCandidateBones=15,
    repeatedSendPreservedEdits=True, reconnectPreservedEdits=True, newProjectAddsRig=True,
    editModeDefersImport=True, networkReturnAcknowledged=True, fullBodyAnchorCount=106,
    standaloneAddonInstalled=addon.endswith(".zip"), gameMotionVerified=False)
(output / "blender-report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
bridge.unregister()
print("CSW_NETWORK_BLENDER_PASSED", flush=True)
