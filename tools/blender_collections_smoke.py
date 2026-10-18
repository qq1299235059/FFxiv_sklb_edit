"""Real Blender checks for native editing, collection repair, and protected-body status."""
import json
from pathlib import Path
import sys
import bpy
from mathutils import Vector

source, addon, output = [Path(p).resolve() for p in sys.argv[sys.argv.index("--")+1:]]
output.mkdir(parents=True, exist_ok=True)
if addon.suffix == ".zip":
    assert bpy.ops.preferences.addon_install(filepath=str(addon), overwrite=True) == {"FINISHED"}
    assert bpy.ops.preferences.addon_enable(module="clothing_skeleton_bridge") == {"FINISHED"}
    bridge = sys.modules["clothing_skeleton_bridge"]
else:
    sys.path.insert(0, str(addon.parent))
    import clothing_skeleton_bridge as bridge
    bridge.register()
bridge.connection.bridge.stop();bridge.connection.bridge.cache=output/"cache"
arm=bridge.import_project(source/"skeleton.gltf")
arm["csw_network_revision"]="f"*64
helper=bridge.anchors
assert helper.check_status(arm)["ok"], helper.check_status(arm)
collections=helper.bone_collections(arm)
assert len(collections["body"].bones)==106 and len(collections["equipment"].bones)==10
assert len(collections["custom"].bones)==0
assert arm.data.collections.active==collections["custom"]
assert "CSW_OT_add_bone" not in dir(bpy.types) and "CSW_OT_add_chain" not in dir(bpy.types)

bpy.ops.object.mode_set(mode="EDIT")
def select(bone):
    for b in arm.data.edit_bones:
        b.select=b.select_head=b.select_tail=False
    bone.select=bone.select_head=bone.select_tail=True
    arm.data.edit_bones.active=bone

# Native E from an original body joint can inherit protected metadata.
# The new bones must become authored bones without weakening the original.
chain=[];parent=arm.data.edit_bones["j_te_l"]
for index in range(4):
    select(parent)
    before=set(arm.data.edit_bones.keys())
    assert bpy.ops.armature.extrude()=={"FINISHED"}
    new=next(b for b in arm.data.edit_bones if b.name not in before)
    new.name=f"csw_native_left_{index}"
    new.tail=new.head+Vector((0,.04,0))
    chain.append(new.name);parent=new
helper.sync_collections(arm)
assert len(helper.authored(arm))==4
assert all(not helper.is_anchor(arm.data.edit_bones[n]) for n in chain)
assert all(collections["custom"] in list(arm.data.edit_bones[n].collections) for n in chain)
assert len({b["cswBoneId"] for b in arm.data.edit_bones})==len(arm.data.edit_bones)
if not helper.check_status(arm)["ok"]:
    records=json.loads(arm["csw_body_reference"])["bones"]
    print("EDIT_MATRIX_DELTAS",[(r["name"],max(abs(a-b) for a,b in zip(r["matrix"],[v for row in arm.data.edit_bones[r["name"]].matrix for v in row]))) for r in records if max(abs(a-b) for a,b in zip(r["matrix"],[v for row in arm.data.edit_bones[r["name"]].matrix for v in row]))>1e-5],flush=True)
assert helper.check_status(arm)["ok"],helper.check_status(arm)

# Native Shift+A plus manual parenting, and native Shift+D of a custom bone.
assert bpy.ops.armature.bone_primitive_add(name="csw_native_added")=={"FINISHED"}
added=arm.data.edit_bones["csw_native_added"];added.parent=arm.data.edit_bones["j_te_r"]
helper.sync_collections(arm)
select(added);before=set(arm.data.edit_bones.keys())
assert bpy.ops.armature.duplicate()=={"FINISHED"}
copy=next(b for b in arm.data.edit_bones if b.name not in before)
copy.name="csw_native_duplicate"
helper.sync_collections(arm)
assert len(helper.authored(arm))==6 and added["cswBoneId"]!=copy["cswBoneId"]
assert helper.check_status(arm)["ok"],helper.check_status(arm)
# Remove extra authoring probes to retain the original 15-node hand-chain fixture.
arm.data.edit_bones.remove(copy);arm.data.edit_bones.remove(added)

root=arm.data.edit_bones[chain[0]]
collections["body"].assign(root);collections["custom"].unassign(root)
helper.sync_collections(arm)
state=helper.check_status(arm)
assert not state["ok"] and any("误放入" in e for e in state["errors"])
mode=arm.mode;parent=root.parent.name;matrix=root.matrix.copy()
assert bpy.ops.csw.arrange_custom()=={"FINISHED"}
assert arm.mode==mode and root.parent.name==parent and root.matrix==matrix
assert helper.check_status(arm)["ok"]
# Legitimate parenting under a body joint is accepted; altering a body parent is rejected.
body=arm.data.edit_bones["j_te_r"];saved_parent=body.parent
body.parent=arm.data.edit_bones["j_ude_a_r"]
if body.parent==saved_parent:body.parent=arm.data.edit_bones["j_sebo_c"]
assert not helper.check_status(arm)["ok"]
assert any("人物原骨父级" in e for e in helper.check_status(arm)["errors"])
body.parent=saved_parent
saved=body.matrix.copy();body.head.x+=.02
assert any("人物原骨静止姿态" in e for e in helper.check_status(arm)["errors"])
body.matrix=saved
assert helper.check_status(arm)["ok"],helper.check_status(arm)
bpy.ops.object.mode_set(mode="OBJECT")
collections["body"].is_visible=False
returned=bridge.export_project(arm)
assert len(json.loads(returned.read_text(encoding="utf-8"))["skins"][0]["joints"])==15
before=returned.read_bytes()
root=arm.data.bones[chain[0]];collections["body"].assign(root)
try:bridge.export_project(arm)
except ValueError as e:assert "状态检查" in str(e)
else:raise AssertionError("Invalid body membership must block export")
assert returned.read_bytes()==before
assert bpy.ops.csw.arrange_custom()=={"FINISHED"}
assert helper.check_status(arm)["ok"]
# Migration reuses the original import matrices, not an edited body pose.
del arm["csw_body_reference"]
bpy.ops.object.mode_set(mode="EDIT")
body=arm.data.edit_bones["j_te_r"];saved=body.matrix.copy();body.head.x+=.02
helper.capture_body_reference(arm,json.loads((source/"reference.ffxiv.json").read_text(encoding="utf-8-sig")))
assert not helper.check_status(arm)["ok"]
body.matrix=saved
bpy.ops.object.mode_set(mode="OBJECT")
assert helper.check_status(arm)["ok"]
report=dict(state="passed",version=".".join(map(str,bridge.bl_info["version"])),bodyBoneCount=106,equipmentOriginalBoneCount=10,
    managedCollections=3,nativeExtrusionVerified=True,nativeAddVerified=True,nativeDuplicateVerified=True,
    oldAddButtonsRemoved=True,wrongMembershipDetected=True,oneClickRepairPreservesModePoseAndParent=True,
    legalBodyParentingAccepted=True,changedOriginalBodyParentRejected=True,changedOriginalBodyPoseRejected=True,
    hiddenBodyCollectionDoesNotDropBones=True,invalidMembershipBlocksReturn=True,previousValidReturnPreserved=True,
    legacyBodyReferenceMigrationVerified=True,candidateBoneCount=15,gameMotionVerified=False)
(output/"report.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
(output/"returned-file.txt").write_text(str(returned),encoding="utf-8")
bridge.unregister()
print("CSW_COLLECTIONS_PASSED",flush=True)
