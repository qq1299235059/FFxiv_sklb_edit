"""Mod-scoped models: separate equipment rigs and self-contained garment files."""
from contextlib import contextmanager
import base64
import hashlib
import json
from pathlib import Path
import re
import tempfile
import uuid

import bpy
from mathutils import Matrix
from . import anchors


def project_id(primary):
    return json.loads(primary["csw_baseline"])["projectId"]


def part_id(path):
    return hashlib.sha256(path.encode("utf-8")).hexdigest()[:16]


def _collection(name, parent):
    collection = bpy.data.collections.new(name)
    parent.children.link(collection)
    return collection


def _move_objects(objects, collection):
    for obj in objects:
        collection.objects.link(obj)
        for previous in list(obj.users_collection):
            if previous != collection:
                previous.objects.unlink(obj)


def parts(primary):
    identity = project_id(primary)
    result = [primary] if primary.get("csw_part_id") else []
    return result + [obj for obj in bpy.context.scene.objects if obj.type == "ARMATURE" and obj.get("csw_aux_part") and obj.get("csw_part_project_id") == identity]


def _tag_aux(primary, rig, item):
    rig["csw_aux_part"] = True
    rig["csw_part_project_id"] = project_id(primary)
    rig["csw_part_id"] = part_id(item["gamePath"])
    rig["csw_part_game_path"] = item["gamePath"]
    rig["csw_part_label"] = item["label"]
    rig.csw_include_part = True
    body = {target["boneName"] for target in anchors.info(primary)["targets"]}
    # Model-only primary sources already contain the body as protected originals.
    if not body:
        body = {bone["name"] for bone in json.loads(primary["csw_baseline"])["bones"]}
    originals = []
    for bone in rig.data.bones:
        for key in list(bone.keys()):
            if key.startswith("csw"):
                del bone[key]
        bone["cswBoneId"] = str(uuid.uuid4())
        bone["cswImportMatrix"] = [value for row in bone.matrix_local for value in row]
        bone["cswOriginalIndex"] = len(originals)
        if bone.name in body:
            bone["cswAnchorBone"] = bone.name
    for bone in rig.data.bones:
        originals.append(dict(id=bone["cswBoneId"], name=bone.name,
                              parentId=bone.parent.get("cswBoneId") if bone.parent else None,
                              originalIndex=bone["cswOriginalIndex"], matrix=list(bone["cswImportMatrix"])))
    baseline = dict(projectId=project_id(primary), sourceSha256="model-reference", bones=originals)
    rig["csw_baseline"] = json.dumps(baseline)
    descriptor = dict(available=True, referenceSha256="model-reference", targets=[], existingAnchors=[], maxConnections=4)
    rig["csw_anchor_rig_info"] = json.dumps(descriptor)
    rig["csw_body_reference"] = json.dumps(dict(sourceSha256="model-reference", bones=[]))
    anchors.sync_collections(rig)


def import_models(primary, directory):
    directory = Path(directory)
    manifest_file = directory / "mod-project.json"
    if not manifest_file.is_file():
        return
    mod = json.loads(manifest_file.read_text(encoding="utf-8-sig"))
    primary["csw_mod_id"] = mod["id"]
    primary["csw_mod_name"] = mod["name"]
    primary["csw_part_id"] = part_id(mod["targetModelGamePath"])
    primary["csw_part_game_path"] = mod["targetModelGamePath"]
    primary["csw_part_label"] = "主要服装"
    primary.csw_export_mode = "FULL" if mod.get("exportClothing") else "SKELETON"
    primary.csw_include_part = True
    root = _collection("Mod · " + mod["name"], bpy.context.scene.collection)
    root["csw_project_id"] = project_id(primary)
    primary["csw_mod_collection"] = root.name
    working = _collection("服装部件", root)
    primary_collection = _collection("主要服装 · 骨架", working)
    owned = [primary] + [obj for obj in bpy.context.scene.objects if obj.get("csw_proxy") and any(m.type == "ARMATURE" and m.object == primary for m in obj.modifiers)]
    _move_objects(owned, primary_collection)
    groups = {"clothing": working, "body": _collection("参考 · 身体与头部", root), "equipment": _collection("参考 · 穿着装备", root), "weapons": _collection("参考 · 武器", root)}
    manifest = directory / "models.json"
    if not manifest.is_file():
        return
    data = json.loads(manifest.read_text(encoding="utf-8-sig"))
    if data["projectId"] != project_id(primary) or data["modId"] != mod["id"]:
        raise ValueError("模型资料不属于当前 Mod")
    for item in data["models"]:
        if not re.fullmatch(r"ref-[0-9a-f]{16}\.glb", item["file"]):
            raise ValueError("模型交换文件名无效")
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=str(directory / item["file"]), bone_heuristic="BLENDER", guess_original_bind_pose=False)
        created = set(bpy.data.objects) - before
        rigs = [obj for obj in created if obj.type == "ARMATURE"]
        shapes = {bone.custom_shape for rig in rigs for bone in rig.pose.bones if bone.custom_shape}
        for rig in rigs:
            for bone in rig.pose.bones:
                bone.custom_shape = None
        for shape in shapes & created:
            shape["csw_proxy"] = True
            shape.hide_set(True)
        editable = item.get("editable", False)
        group = groups[item["group"]]
        is_primary = editable and item["gamePath"] == mod["targetModelGamePath"]
        collection = primary_collection if is_primary else _collection(item["label"] + " · " + item["id"][:6], group)
        _move_objects(created, collection)
        if editable:
            if len(rigs) != 1:
                raise ValueError("每个可编辑装备部件需要独立且唯一的骨架")
            source_rig = rigs[0]
            if is_primary:
                for mesh in (obj for obj in created if obj.type == "MESH" and not obj.get("csw_proxy")):
                    world = mesh.matrix_world.copy()
                    if mesh.parent == source_rig:
                        mesh.parent = primary; mesh.matrix_world = world
                    for modifier in mesh.modifiers:
                        if modifier.type == "ARMATURE" and modifier.object == source_rig:
                            modifier.object = primary
                created.remove(source_rig)
                bpy.data.objects.remove(source_rig, do_unlink=True)
                rig = primary
            else:
                rig = source_rig
                _tag_aux(primary, rig, item)
                rig.name = item["label"] + " · 独立骨架"
            rig.show_in_front = True
            for mesh in (obj for obj in created if obj.type == "MESH" and not obj.get("csw_proxy")):
                mesh["csw_garment_project"] = project_id(primary)
                mesh["csw_part_id"] = rig["csw_part_id"]
        else:
            for obj in created:
                obj["csw_reference"] = True
                obj.hide_select = True
                if obj.type == "ARMATURE":
                    obj.hide_set(True)
    bpy.ops.object.select_all(action="DESELECT")
    primary.select_set(True);bpy.context.view_layer.objects.active = primary


def sync(primary):
    for rig in parts(primary):
        if rig != primary:
            anchors.sync_collections(rig)


def check(primary):
    errors = []
    identities = set()
    for rig in parts(primary):
        key = rig["csw_part_id"]
        if key in identities:
            errors.append("同一装备部件存在两套可发送骨架，请保留一套或修改替换路径")
        identities.add(key)
        state = anchors.check_status(rig)
        errors.extend(rig.get("csw_part_label", rig.name) + "：" + error for error in state["errors"])
    return errors


def garment_meshes(primary, rig):
    return [obj for obj in bpy.context.scene.objects if obj.type == "MESH" and obj.get("csw_garment_project") == project_id(primary) and obj.get("csw_part_id") == rig["csw_part_id"] and not obj.get("csw_proxy") and not obj.get("csw_reference")]


def mark_selected(primary):
    candidates = [obj for obj in bpy.context.selected_objects if obj.type == "MESH" and not obj.get("csw_proxy") and not obj.get("csw_reference")]
    if not candidates:
        raise ValueError("请先选中准备实装的服装网格，参考模型不能直接作为返回对象")
    current = bpy.context.object
    rig = next((m.object for m in current.modifiers if m.type == "ARMATURE" and m.object and m.object in parts(primary)), None) if current and current.type == "MESH" else None
    rig = next((obj for obj in bpy.context.selected_objects if obj.type == "ARMATURE" and obj in parts(primary)), None) or primary.csw_garment_target or rig or primary
    for mesh in candidates:
        modifiers = [m for m in mesh.modifiers if m.type == "ARMATURE"]
        if len(modifiers) > 1:
            raise ValueError("服装网格不能同时绑定多套骨架")
        modifier = modifiers[0] if modifiers else mesh.modifiers.new("服装骨架", "ARMATURE")
        modifier.object = rig
        world = mesh.matrix_world.copy();mesh.parent = rig;mesh.matrix_world = world
        _move_objects([mesh], rig.users_collection[0])
        mesh["csw_garment_project"] = project_id(primary)
        mesh["csw_part_id"] = rig["csw_part_id"]
    return len(candidates)


@contextmanager
def merged_skeleton(primary):
    """Equipment rigs stay independent in the scene; combine new chains for the game resource."""
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    sync(primary)
    errors = check(primary)
    if errors:
        raise ValueError(errors[0])
    extras = [(rig, bone) for rig in parts(primary) if rig != primary for bone in anchors.authored(rig)]
    total = len(anchors.authored(primary)) + len(extras)
    if total and not anchors.info(primary).get("allowNewBones", True):
        raise ValueError("当前 Mod 仅制作服装模型；新增骨骼前请在游戏选择装备附加骨架模板")
    if not extras:
        yield primary
        return
    names = set(primary.data.bones.keys())
    for rig, bone in extras:
        if bone.name in names:
            raise ValueError("不同装备的新骨名称重复，请分别命名：" + bone.name)
        names.add(bone.name)
        if not bone.parent or bone.parent.name not in names | {b.name for _, b in extras}:
            raise ValueError("新增骨链需要挂到人物挂点或本 Mod 的服装骨骼：" + bone.name)
    before = list(bpy.context.selected_objects);active = bpy.context.view_layer.objects.active
    clone = primary.copy();clone.data = primary.data.copy();data = clone.data
    bpy.context.scene.collection.objects.link(clone)
    proxies = []
    try:
        for obj in list(bpy.context.scene.objects):
            if obj.get("csw_proxy") and any(m.type == "ARMATURE" and m.object == primary for m in obj.modifiers):
                proxy = obj.copy();bpy.context.scene.collection.objects.link(proxy)
                if proxy.parent == primary:
                    proxy.parent = clone
                for modifier in proxy.modifiers:
                    if modifier.type == "ARMATURE" and modifier.object == primary:
                        modifier.object = clone
                proxies.append(proxy)
        bpy.ops.object.select_all(action="DESELECT");clone.select_set(True);bpy.context.view_layer.objects.active = clone
        bpy.ops.object.mode_set(mode="EDIT")
        for rig, source in extras:
            bone = clone.data.edit_bones.new(source.name)
            bone.matrix = primary.matrix_world.inverted() @ rig.matrix_world @ source.matrix_local
            bone.length = max(source.length, .015)
            bone["cswBoneId"] = source["cswBoneId"];bone["cswOriginalIndex"] = -1
        for rig, source in extras:
            bone = clone.data.edit_bones[source.name]
            parent = clone.data.edit_bones.get(source.parent.name) if source.parent else None
            if not parent:
                raise ValueError("此新增骨链挂在非基础人物骨骼上，当前装备附加骨架无法连接：" + source.name)
            bone.parent = parent;bone.use_connect = False
        bpy.ops.object.mode_set(mode="OBJECT")
        anchors.sync_collections(clone)
        yield clone
    finally:
        if bpy.context.object and bpy.context.object.mode != "OBJECT":
            bpy.ops.object.mode_set(mode="OBJECT")
        for obj in proxies:
            bpy.data.objects.remove(obj, do_unlink=True)
        bpy.data.objects.remove(clone, do_unlink=True);bpy.data.armatures.remove(data)
        bpy.ops.object.select_all(action="DESELECT")
        for obj in before:
            if obj.name in bpy.context.scene.objects:
                obj.select_set(True)
        bpy.context.view_layer.objects.active = active


def _embed(gltf, directory):
    for buffer in gltf.get("buffers", []):
        path = (directory / buffer["uri"]).resolve()
        path.relative_to(directory.resolve())
        buffer["uri"] = "data:application/octet-stream;base64," + base64.b64encode(path.read_bytes()).decode("ascii")
    for image in gltf.get("images", []):
        if "uri" not in image or image["uri"].startswith("data:"):
            continue
        path = (directory / image["uri"]).resolve();path.relative_to(directory.resolve())
        mime = "image/jpeg" if path.suffix.lower() in {".jpg", ".jpeg"} else "image/png"
        image["uri"] = "data:" + mime + ";base64," + base64.b64encode(path.read_bytes()).decode("ascii")


def _clean(value):
    if isinstance(value, dict):
        if "extras" in value:
            safe = {k: v for k, v in value["extras"].items() if k.startswith("atr_") and v is True}
            if safe:
                value["extras"] = safe
            else:
                del value["extras"]
        for child in value.values():
            _clean(child)
    elif isinstance(value, list):
        for child in value:
            _clean(child)


def export_garments(primary, directory):
    directory = Path(directory);items = []
    selected = list(bpy.context.selected_objects);active = bpy.context.view_layer.objects.active
    try:
        for rig in parts(primary):
            if not rig.csw_include_part:
                continue
            meshes = garment_meshes(primary, rig)
            if not meshes:
                continue
            for mesh in meshes:
                modifiers = [m for m in mesh.modifiers if m.type == "ARMATURE"]
                if len(modifiers) != 1 or modifiers[0].object != rig:
                    raise ValueError("服装部件绑定了其他装备骨架：" + mesh.name)
                if not mesh.vertex_groups or any(not any(g.weight > 0 and mesh.vertex_groups[g.group].name in rig.data.bones for g in vertex.groups) for vertex in mesh.data.vertices):
                    raise ValueError("服装存在未绑定权重的顶点：" + mesh.name)
                if not mesh.data.uv_layers:
                    raise ValueError("服装网格缺少 UV：" + mesh.name)
                if not mesh.data.materials:
                    raise ValueError("服装网格缺少材质槽：" + mesh.name)
            game_path = rig["csw_part_game_path"];identity = part_id(game_path)
            with tempfile.TemporaryDirectory(prefix=".cloth-", dir=directory) as temporary:
                path = Path(temporary) / ("cloth-" + identity + ".gltf")
                bpy.ops.object.select_all(action="DESELECT")
                for obj in [rig] + meshes:
                    obj.select_set(True)
                bpy.context.view_layer.objects.active = rig
                bpy.ops.export_scene.gltf(filepath=str(path), export_format="GLTF_SEPARATE", use_selection=True,
                    export_extras=True, export_attributes=True, export_animations=False, export_def_bones=False, export_skins=True,
                    export_yup=True, export_tangents=True, export_try_sparse_sk=False)
                gltf = json.loads(path.read_text(encoding="utf-8"));_clean(gltf);_embed(gltf, path.parent)
                if len(gltf.get("skins", [])) != 1:
                    raise ValueError("每个装备部件必须各自绑定一套骨架")
                output = directory / path.name
                output.write_text(json.dumps(gltf, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
                items.append(dict(id=identity, name=rig.get("csw_part_label", "服装"), gamePath=game_path, file=output.name, sha256=hashlib.sha256(output.read_bytes()).hexdigest()))
    finally:
        bpy.ops.object.select_all(action="DESELECT")
        for obj in selected:
            obj.select_set(True)
        bpy.context.view_layer.objects.active = active
    if not items:
        raise ValueError("没有选择要发送的服装部件，请先导入并标记服装网格，或切换为仅骨骼")
    return items
