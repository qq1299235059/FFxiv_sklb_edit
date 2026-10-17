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


def used(armature):
    return {bone.parent.name for bone in armature.data.bones if not is_anchor(bone) and bone.parent and is_anchor(bone.parent)}


def authored(armature):
    original = {bone["id"] for bone in json.loads(armature["csw_baseline"])["bones"]}
    return [bone for bone in armature.data.bones if not is_anchor(bone) and bone.get("cswBoneId") not in original]


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
    collection = armature.data.collections.new("人体挂点（未使用时自动忽略）")
    for name in created:
        collection.assign(armature.data.bones[name])
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
