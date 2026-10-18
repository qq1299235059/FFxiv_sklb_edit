"""Author two independently parented chains, prune unused body anchors, preserve the scene."""
import hashlib
import json
from pathlib import Path
import runpy
import sys

import bpy
from mathutils import Matrix, Vector

args=sys.argv[sys.argv.index("--")+1:]
source,plugin,report_path=(Path(v).resolve() for v in args[:3])
sys.argv=[sys.argv[0],"--",str(source/"skeleton.gltf")]
runpy.run_path(str(plugin/"BlenderBridge/blender_open_project.py"))
bridge=sys.modules["clothing_skeleton_bridge"]
arm=bridge.current_armature(bpy.context)
helper=bridge.anchors
baseline=json.loads(arm["csw_baseline"])
descriptor=helper.info(arm)
assert descriptor["characterSource"]["selectionKind"] in {"self","target"}
assert descriptor["characterSource"]["origin"]=="selected-character-original-sqpack"
assert descriptor["characterSource"]["gamePath"]==descriptor["referenceGamePath"]
assert len(baseline["bones"])==10
body=[b for b in arm.data.bones if helper.is_anchor(b)]
body_names={b.name for b in body}
assert len(body)==106 and len(arm.data.bones)==116
assert {"j_te_l","j_te_r","j_ude_a_l","j_asi_c_r","j_kubi"}.issubset(arm.data.bones.keys())
original={b["name"]:arm.data.bones[b["name"]].matrix_local.copy() for b in baseline["bones"]}
worlds={}
chains=[]
for target in ("j_te_l","j_te_r"):
    # Start on an equipment bone, then perform the same parenting operation as
    # Edit Mode / Ctrl+P / Keep Offset. This tests manual parenting, not a selector.
    names=bridge.add_test_chain(arm,baseline["bones"][0]["name"])
    chains.append(names)
    bpy.ops.object.mode_set(mode="EDIT")
    root=arm.data.edit_bones[names[0]]
    parent=arm.data.edit_bones[target]
    offset=parent.head-root.head
    for name in names:
        bone=arm.data.edit_bones[name]
        bone.head+=offset
        bone.tail+=offset
    root.parent=parent
    root.use_connect=False
    bpy.ops.object.mode_set(mode="OBJECT")
    for name in names:
        worlds[name]=arm.data.bones[name].head_local.copy()
assert helper.used(arm)=={"j_te_l","j_te_r"}
count_before=len(arm.data.bones)
returned=bridge.export_project(arm)
assert len(arm.data.bones)==count_before and len([b for b in arm.data.bones if helper.is_anchor(b)])==106
assert all(arm.data.bones[name].matrix_local==matrix for name,matrix in original.items())
assert arm.matrix_world==Matrix.Identity(4)
gltf=json.loads(returned.read_text(encoding="utf-8"))
bindings=gltf["extras"]["cswAnchorBindings"]
assert {b["boneName"] for b in bindings}=={"j_te_l","j_te_r"}
assert {b["referenceBoneIndex"] for b in bindings}=={64,65}
joint_nodes={i:gltf["nodes"][i] for i in gltf["skins"][0]["joints"]}
names={node["name"] for node in joint_nodes.values()}
assert len(names)==20 and len(joint_nodes)==20
assert not (body_names-{"j_te_l","j_te_r"}) & names
parents={child:i for i,node in enumerate(gltf["nodes"]) for child in node.get("children",[])}
cache={}
def world(i):
    if i in cache:return cache[i]
    m=helper.node_matrix(gltf["nodes"][i])
    if i in parents:m=world(parents[i])@m
    cache[i]=m
    return m
basis=Matrix(((1,0,0,0),(0,0,-1,0),(0,1,0,0),(0,0,0,1)))
for i,node in joint_nodes.items():
    if node["name"] in worlds:
        point=(basis@world(i)).translation
        assert (point-worlds[node["name"]]).length<1e-4,(node["name"],point,worlds[node["name"]])
for chain,target in zip(chains,("j_te_l","j_te_r")):
    i=next(i for i,n in joint_nodes.items() if n["name"]==chain[0])
    assert gltf["nodes"][parents[i]]["name"]==target
manifest=json.loads(returned.with_suffix(".complete.json").read_text())
assert manifest["boneCount"]==20 and manifest["gltfSha256"]==hashlib.sha256(returned.read_bytes()).hexdigest()
# An edited but unused helper must not enter the file or block pruning.
bpy.ops.object.mode_set(mode="EDIT")
unused=arm.data.edit_bones["n_buki_r"]
saved_head,saved_tail=unused.head.copy(),unused.tail.copy()
unused.head.x+=.02
unused.tail.x+=.02
bpy.ops.object.mode_set(mode="OBJECT")
bridge.export_project(arm)
assert "n_buki_r" not in {n.get("name") for n in json.loads(returned.read_text())["nodes"]}
bpy.ops.object.mode_set(mode="EDIT")
arm.data.edit_bones["n_buki_r"].head=saved_head
arm.data.edit_bones["n_buki_r"].tail=saved_tail
bpy.ops.object.mode_set(mode="OBJECT")
# Reuse the pre-existing chest connection instead of adding a fourth root.
extra=bridge.add_test_chain(arm,"j_sebo_c")
bridge.export_project(arm)
alias_gltf=json.loads(returned.read_text())
assert len(alias_gltf["skins"][0]["joints"])==24 and len(alias_gltf["extras"]["cswAnchorBindings"])==2
alias_nodes=alias_gltf["nodes"]
alias_parent={child:i for i,node in enumerate(alias_nodes) for child in node.get("children",[])}
alias_root=next(i for i,n in enumerate(alias_nodes) if n.get("name")==extra[0])
assert alias_nodes[alias_parent[alias_root]]["name"]==baseline["bones"][0]["name"]
bpy.ops.object.mode_set(mode="EDIT")
for name in extra:arm.data.edit_bones.remove(arm.data.edit_bones[name])
# Test the actual add-chain operator with a body anchor active in Edit Mode.
for bone in arm.data.edit_bones:bone.select=False
arm.data.edit_bones["j_te_l"].select=True
arm.data.edit_bones.active=arm.data.edit_bones["j_te_l"]
existing_names=set(arm.data.edit_bones.keys())
assert bpy.ops.csw.add_chain()=={"FINISHED"}
operator_names=set(arm.data.edit_bones.keys())-existing_names
assert len(operator_names)==4
bpy.ops.object.mode_set(mode="OBJECT")
operator_root=next(b for b in arm.data.bones if b.name in operator_names and b.parent and b.parent.name=="j_te_l")
assert operator_root
bridge.export_project(arm)
assert len(json.loads(returned.read_text())["extras"]["cswAnchorBindings"])==2
bpy.ops.object.mode_set(mode="EDIT")
for name in operator_names:arm.data.edit_bones.remove(arm.data.edit_bones[name])
bpy.ops.object.mode_set(mode="OBJECT")
bridge.export_project(arm)
before=returned.read_bytes()
bpy.ops.object.mode_set(mode="EDIT")
arm.data.edit_bones[chains[0][0]].parent=None
bpy.ops.object.mode_set(mode="OBJECT")
try:bridge.export_project(arm)
except ValueError:pass
else:raise AssertionError("An unparented new chain must be rejected")
assert returned.read_bytes()==before
# Restore the scene to a useful complete rig after the negative case.
bpy.ops.object.mode_set(mode="EDIT")
arm.data.edit_bones[chains[0][0]].parent=arm.data.edit_bones["j_te_l"]
bpy.ops.object.mode_set(mode="OBJECT")
report=dict(state="passed",originalBoneCount=10,selectableBodyAnchorCount=106,authoredBoneCount=8,
            selectedCharacterSourceImported=True,noPresetBodyFallback=True,
            usedAnchorCount=2,ignoredAnchorCount=104,candidateBoneCount=20,manualParentingVerified=True,
            bothHandsIndependent=True,exportedWorldPositionsPreserved=True,originalRestPosesPreserved=True,
            authoringSceneUnchangedByExport=True,unparentedChainRejected=True,failedReturnPreserved=True,
            unusedEditedAnchorIgnored=True,existingAttachmentReused=True,addOperatorUsesActiveBodyAnchor=True,
            gameMotionVerified=False)
report_path.parent.mkdir(parents=True,exist_ok=True)
report_path.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding="utf-8")
print("CSW_FULL_ANCHOR_RIG_PASSED",flush=True)
