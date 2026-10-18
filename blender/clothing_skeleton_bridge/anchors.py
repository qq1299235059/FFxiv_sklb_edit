"""Selectable authoring anchors; only used anchors enter the equipment file."""
from contextlib import contextmanager
import json
import uuid

import bpy
from mathutils import Matrix, Quaternion, Vector


def info(armature):
    value = armature.get("csw_anchor_rig_info")
    return json.loads(value) if value else None


def is_anchor(bone):
    return bool(bone.get("cswAnchorBone"))


def editable_bones(armature):
    return armature.data.edit_bones if armature.mode == "EDIT" else armature.data.bones


def used(armature):
    return {bone.parent.name for bone in editable_bones(armature) if not is_anchor(bone) and bone.parent and is_anchor(bone.parent)}


def authored(armature):
    original = {bone["id"] for bone in json.loads(armature["csw_baseline"])["bones"]}
    return [bone for bone in editable_bones(armature) if not is_anchor(bone) and bone.get("cswBoneId") not in original]



COLLECTIONS = (("body", "人物骨骼"), ("equipment", "服装原骨"), ("custom", "服装骨骼"))


def bone_collections(armature):
    result = {}
    for role, name in COLLECTIONS:
        collection = next((c for c in armature.data.collections_all if c.get("csw_role") == role), None)
        if collection is None:
            collection = armature.data.collections.get(name)
        if collection is None and role == "body":
            collection = armature.data.collections.get("人体挂点（未使用时自动忽略）")
        if collection is None:
            collection = armature.data.collections.new(name)
        collection.name = name
        collection["csw_role"] = role
        result[role] = collection
    return result


def sync_collections(armature):
    """Classify native Edit Mode additions without changing protected bones or the mode.

    Extrusion and duplication can copy the parent's custom properties. When the
    source bone still exists, only the new copy receives a fresh authoring ID.
    A renamed or otherwise edited original stays protected and fails validation.
    """
    if not armature or armature.type != "ARMATURE" or "csw_baseline" not in armature:
        return
    bones = editable_bones(armature)
    original = {bone["id"]: bone["name"] for bone in json.loads(armature["csw_baseline"])["bones"]}
    groups = {}
    for bone in bones:
        identity = bone.get("cswBoneId")
        if identity:
            groups.setdefault(identity, []).append(bone)
    for identity, copies in groups.items():
        if len(copies) < 2:
            continue
        source = next((bone for bone in copies if original.get(identity) == bone.name), None)
        if source is None:
            source = next((bone for bone in copies if bone.get("cswAnchorBone") == bone.name), None)
        if source is None:
            # Native extrusion appends the duplicate after its source. An explicit
            # stable owner name also survives ordinary rename/duplicate workflows.
            source = next((bone for bone in copies if bone.get("cswAuthoredName") == bone.name), copies[0])
        for bone in copies:
            if bone != source:
                for key in list(bone.keys()):
                    if key.startswith("csw"):
                        del bone[key]
                bone["cswBoneId"] = str(uuid.uuid4())
                bone["cswOriginalIndex"] = -1
                bone.use_deform = True
    collections = bone_collections(armature)
    first = armature.get("csw_collections_version", 0) < 1
    for bone in bones:
        identity = bone.get("cswBoneId")
        if not identity:
            bone["cswBoneId"] = identity = str(uuid.uuid4())
            bone["cswOriginalIndex"] = -1
        role = "body" if is_anchor(bone) else "equipment" if identity in original else "custom"
        if role == "custom":
            bone["cswAuthoredName"] = bone.name
        chosen = collections[role]
        if first or bone.get("cswCollectionRole") != role:
            chosen.assign(bone)
            for previous in list(bone.collections):
                if previous != chosen and (first or previous.get("csw_role") in collections):
                    previous.unassign(bone)
            bone["cswCollectionRole"] = role
    if first:
        armature.data.collections.active = collections["custom"]
        armature["csw_collections_version"] = 1



def capture_body_reference(armature, reference):
    """Keep the imported original body contract separate from editable membership."""
    descriptor = info(armature) or {}
    if reference.get("sourceSha256") != descriptor.get("referenceSha256"):
        raise ValueError("人物原骨对照来源不一致，请重新发送骨架")
    originals = {b["name"]: b["id"] for b in json.loads(armature["csw_baseline"])["bones"]}
    by_id = {b["id"]: b for b in reference["bones"]}
    current = {b.get("cswBoneId"): b for b in editable_bones(armature)}
    records = []
    for source in reference["bones"]:
        if source["name"] in originals:
            continue
        identity = str(uuid.uuid5(uuid.UUID(descriptor["projectId"]), "body-anchor:" + source["name"]))
        parent = by_id.get(source["parentId"])
        parent_id = (originals.get(parent["name"]) or str(uuid.uuid5(uuid.UUID(descriptor["projectId"]), "body-anchor:" + parent["name"]))) if parent else None
        bone = current.get(identity)
        matrix = list(bone.get("cswImportMatrix", [])) if bone else []
        records.append(dict(id=identity, name=source["name"], parentId=parent_id, matrix=matrix))
    armature["csw_body_reference"] = json.dumps(dict(sourceSha256=reference["sourceSha256"], bones=records))


def check_status(armature):
    """Membership and original-body hierarchy are independent checks.

    A custom bone parented to a body anchor is expected. A custom bone assigned
    to the body collection, or an original body bone reparented under it, is not.
    """
    bones = list(editable_bones(armature))
    collections = bone_collections(armature)
    original = json.loads(armature["csw_baseline"])["bones"]
    original_ids = {b["id"] for b in original}
    errors = []
    for bone in bones:
        role = "body" if is_anchor(bone) else "equipment" if bone.get("cswBoneId") in original_ids else "custom"
        membership = list(bone.collections)
        if role == "custom" and bone.parent is None:
            errors.append(f"新增服装骨链没有父级：{bone.name}，请手动指定父级")
        if role != "body" and collections["body"] in membership:
            errors.append(f"服装骨骼 {bone.name} 误放入“人物骨骼”集合")
        elif collections[role] not in membership or any(c != collections[role] and c.get("csw_role") in collections for c in membership):
            errors.append(f"{bone.name} 集合归属错误，应位于“{collections[role].name}”")
    by_id = {}
    for bone in bones:
        identity = bone.get("cswBoneId")
        if identity in by_id:
            errors.append(f"骨骼身份重复：{bone.name}")
        by_id[identity] = bone
    contract = armature.get("csw_body_reference")
    if not contract:
        errors.append("缺少人物原骨对照，请从游戏重新发送当前项目")
        expected_body = []
    else:
        reference = json.loads(contract)
        expected_body = reference["bones"]
        if reference["sourceSha256"] != (info(armature) or {}).get("referenceSha256"):
            errors.append("人物原骨对照来源不一致，请重新创建项目")
    for expected in expected_body + original:
        bone = by_id.get(expected["id"])
        label = "人物原骨" if expected["id"] not in original_ids else "服装原骨"
        if bone is None:
            errors.append(f"{label}被删除：{expected['name']}")
            continue
        if bone.name != expected["name"]:
            errors.append(f"{label}被改名：{expected['name']}")
        parent_id = bone.parent.get("cswBoneId") if bone.parent else None
        if parent_id != expected["parentId"]:
            errors.append(f"{label}父级被改变：{expected['name']}，请恢复原人物/装备层级")
        imported = expected.get("matrix") if label == "人物原骨" else bone.get("cswImportMatrix")
        matrix = bone.matrix if armature.mode == "EDIT" else bone.matrix_local
        current = [value for row in matrix for value in row]
        # Blender reconstructs EditBone axes on mode changes (observed ~3e-5).
        # Keep strict positional checks while allowing that axis roundoff in body helpers.
        if imported is None or len(imported) != 16 or any(abs(a-b) > (1e-4 if label == "人物原骨" and i not in {3,7,11} else 1e-5) for i,(a,b) in enumerate(zip(imported,current))):
            errors.append(f"{label}静止姿态被改变：{expected['name']}")
    return dict(ok=not errors, errors=errors, bodyBoneCount=len(expected_body), customBoneCount=len(authored(armature)))


def arrange_custom(armature):
    sync_collections(armature)
    collections = bone_collections(armature)
    custom = authored(armature)
    for bone in custom:
        collections["custom"].assign(bone)
        for previous in list(bone.collections):
            if previous != collections["custom"] and previous.get("csw_role") in collections:
                previous.unassign(bone)
    return len(custom)


def aliases(armature):
    descriptor = info(armature) or {}
    return {entry["boneName"]: entry["rootName"] for entry in descriptor.get("existingAnchors", [])}


def add_reference(armature, objects):
    descriptor = info(armature)
    reference = next((obj for obj in objects if obj.type == "ARMATURE"), None)
    if reference is None or not descriptor or not descriptor.get("available"):
        raise ValueError("项目缺少完整挂点资料，请在新版游戏插件重新创建项目")
    source = [(bone.name, bone.parent.name if bone.parent else None, bone.matrix_local.copy(), bone.length)
              for bone in reference.data.bones]
    targets = {entry["boneName"]: entry for entry in descriptor["targets"]}
    if set(targets) != {row[0] for row in source}:
        raise ValueError("完整挂点资料与人体参考不一致，请重新创建项目")
    bpy.ops.object.select_all(action="DESELECT")
    armature.select_set(True)
    bpy.context.view_layer.objects.active = armature
    bpy.ops.object.mode_set(mode="EDIT")
    created = []
    try:
        for name, parent, matrix, length in source:
            if name in armature.data.edit_bones:
                continue  # Original equipment bones retain their exact identity and parent.
            bone = armature.data.edit_bones.new(name)
            bone.matrix = matrix
            bone.length = max(length, 0.015)
            created.append(name)
        for name, parent, matrix, length in source:
            if name not in created:
                continue
            bone = armature.data.edit_bones[name]
            bone.parent = armature.data.edit_bones.get(parent) if parent else None
            bone.use_connect = False
            bone.matrix = matrix
    finally:
        bpy.ops.object.mode_set(mode="OBJECT")
    for name in created:
        bone = armature.data.bones[name]
        bone["cswAnchorBone"] = name
        bone["cswBoneId"] = str(uuid.uuid5(uuid.UUID(descriptor["projectId"]), "body-anchor:" + name))
        bone["cswOriginalIndex"] = -2
        bone["cswImportMatrix"] = [value for row in bone.matrix_local for value in row]
        bone.use_deform = False
    for obj in objects:
        bpy.data.objects.remove(obj, do_unlink=True)
    armature["csw_anchor_display_count"] = len(targets)


@contextmanager
def export_rig(armature, proxies):
    descriptor = info(armature)
    selected = list(bpy.context.selected_objects)
    active = bpy.context.view_layer.objects.active
    helper_used = used(armature)
    existing = aliases(armature)
    retained = helper_used - set(existing)
    if descriptor and len(descriptor["existingAnchors"]) + len(retained) > descriptor["maxConnections"]:
        raise ValueError(f"当前模板最多新增 {descriptor['maxConnections']-len(descriptor['existingAnchors'])} 个不同人体挂点；同一挂点可挂多条链")
    original = {bone["id"] for bone in json.loads(armature["csw_baseline"])["bones"]}
    for bone in armature.data.bones:
        if bone.name in helper_used:
            imported = bone.get("cswImportMatrix")
            current = [value for row in bone.matrix_local for value in row]
            if bone.name != bone["cswAnchorBone"] or imported is None or any(abs(a-b)>1e-5 for a, b in zip(imported, current)):
                raise ValueError("已使用的人体挂点被改名或移动，请恢复挂点，只编辑新增骨骼")
        elif not is_anchor(bone) and bone.get("cswBoneId") not in original and bone.parent is None:
            raise ValueError("新增骨链没有父级，请选新链根骨，再选目标挂点，Ctrl+P → 保持偏移")
    duplicate = None
    duplicate_data = None
    clone_proxies = []
    try:
        bpy.ops.object.select_all(action="DESELECT")
        duplicate = armature.copy()
        duplicate_data = armature.data.copy()
        duplicate.data = duplicate_data
        bpy.context.scene.collection.objects.link(duplicate)
        duplicate.animation_data_clear()
        duplicate.select_set(True)
        bpy.context.view_layer.objects.active = duplicate
        for bone in duplicate.pose.bones:
            bone.matrix_basis = Matrix.Identity(4)
        bpy.ops.object.mode_set(mode="EDIT")
        try:
            for bone in duplicate.data.edit_bones:
                matrix = bone.matrix.copy()
                if not is_anchor(bone) and bone.parent and is_anchor(bone.parent) and bone.parent.name in existing:
                    bone.parent = duplicate.data.edit_bones[existing[bone.parent.name]]
                    bone.use_connect = False
                    bone.matrix = matrix
                elif bone.name in retained:
                    bone.parent = None
                    bone.use_connect = False
                    bone.matrix = matrix
            for bone in list(duplicate.data.edit_bones):
                if is_anchor(bone) and bone.name not in retained:
                    duplicate.data.edit_bones.remove(bone)
        finally:
            bpy.ops.object.mode_set(mode="OBJECT")
        for bone in duplicate.data.bones:
            bone.use_deform = True
        for proxy in proxies:
            clone = proxy.copy()
            bpy.context.scene.collection.objects.link(clone)
            if clone.parent == armature:
                clone.parent = duplicate
            for modifier in clone.modifiers:
                if modifier.type == "ARMATURE" and modifier.object == armature:
                    modifier.object = duplicate
            clone.select_set(True)
            clone_proxies.append(clone)
        bpy.context.view_layer.update()
        yield duplicate, retained
    finally:
        if bpy.context.object and bpy.context.object.mode != "OBJECT":
            bpy.ops.object.mode_set(mode="OBJECT")
        for obj in clone_proxies:
            bpy.data.objects.remove(obj, do_unlink=True)
        if duplicate:
            bpy.data.objects.remove(duplicate, do_unlink=True)
        if duplicate_data:
            bpy.data.armatures.remove(duplicate_data)
        bpy.ops.object.select_all(action="DESELECT")
        for obj in selected:
            obj.select_set(True)
        bpy.context.view_layer.objects.active = active


def node_matrix(node):
    if "matrix" in node:
        values = node["matrix"]
        return Matrix(tuple(tuple(values[c*4+r] for c in range(4)) for r in range(4)))
    x, y, z, w = node.get("rotation", [0, 0, 0, 1])
    return Matrix.LocRotScale(Vector(node.get("translation", [0, 0, 0])), Quaternion((w, x, y, z)), Vector(node.get("scale", [1, 1, 1])))


def write_matrix(node, matrix):
    position, rotation, scale = matrix.decompose()
    node.pop("matrix", None)
    node.update(translation=list(position), rotation=[rotation.x, rotation.y, rotation.z, rotation.w], scale=list(scale))


def finish_gltf(armature, gltf, retained):
    descriptor = info(armature)
    if not descriptor or not descriptor.get("available"):
        return
    targets = {entry["boneName"]: entry for entry in descriptor["targets"]}
    bindings = []
    for node in gltf["nodes"]:
        name = node.get("name")
        if name not in retained:
            continue
        target = targets[name]
        pose = target["pose"]
        x, y, z, w = pose["rotation"]
        canonical = Matrix.LocRotScale(Vector(pose["translation"]), Quaternion((w, x, y, z)), Vector(pose["scale"]))
        old = node_matrix(node)
        for child in node.get("children", []):
            entry = gltf["nodes"][child]
            write_matrix(entry, canonical.inverted() @ old @ node_matrix(entry))
        write_matrix(node, canonical)
        bindings.append(dict(boneId=armature.data.bones[name]["cswBoneId"], boneName=name, referenceBoneIndex=target["boneIndex"]))
    gltf.setdefault("extras", {}).update(cswAnchorSchema=1, cswAnchorBindings=bindings)
