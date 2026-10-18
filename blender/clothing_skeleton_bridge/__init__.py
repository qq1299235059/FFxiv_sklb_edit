bl_info = {
    "name": "Clothing Skeleton Bridge",
    "author": "ClothingSkeleton contributors",
    "version": (0, 5, 0),
    "blender": (4, 5, 0),
    "location": "3D View > Sidebar > 服装骨架",
    "description": "Import protected FFXIV clothing skeletons and export appended bones",
    "category": "Import-Export",
}

import hashlib
import json
import os
from pathlib import Path
import tempfile
import uuid

import bpy
from bpy.props import StringProperty
from bpy_extras.io_utils import ImportHelper
from mathutils import Vector, Matrix, Quaternion
from . import anchors

PLUGIN_ROOT = None
def configure_plugin_root(directory):
    global PLUGIN_ROOT
    root = Path(directory).resolve()
    if not (root / "ClothingSkeleton.dll").is_file():
        raise ValueError("无法定位插件目录，请从游戏插件重新打开项目")
    PLUGIN_ROOT = root


def _return_file_for_source(filepath):
    if PLUGIN_ROOT is not None:
        return PLUGIN_ROOT / "BlenderReturn" / "edited.gltf"
    descriptor = Path(filepath).parent / "bridge.json"
    if descriptor.is_file():
        info = json.loads(descriptor.read_text(encoding="utf-8-sig"))
        configure_plugin_root(Path(filepath).parent / info["pluginRoot"])
    else:
        configure_plugin_root(Path(__file__).resolve().parents[2])
    return PLUGIN_ROOT / "BlenderReturn" / "edited.gltf"


def import_project(filepath):
    filepath = Path(filepath).resolve()
    fixed_return = _return_file_for_source(filepath)
    baseline = json.loads(filepath.with_suffix(".ffxiv.json").read_text(encoding="utf-8-sig"))
    descriptor_path = filepath.with_name("anchor-rig.json")
    if not descriptor_path.is_file():
        raise ValueError("请在游戏侧读取自己或当前目标，重新创建人物骨架项目")
    descriptor = json.loads(descriptor_path.read_text(encoding="utf-8-sig"))
    character = descriptor.get("characterSource")
    if not descriptor.get("available") or not character:
        raise ValueError("此项目没有可用的场景角色骨架，请在游戏侧选择角色并重新创建项目")
    if character.get("origin") != "selected-character-original-sqpack" or character.get("selectionKind") not in {"self", "target"} or character.get("gamePath") != descriptor.get("referenceGamePath"):
        raise ValueError("人物骨架来源与项目参考不一致，请在游戏侧重新导出")
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=str(filepath), bone_heuristic="BLENDER", guess_original_bind_pose=False)
    created = set(bpy.data.objects) - before
    armatures = [obj for obj in created if obj.type == "ARMATURE"]
    if len(armatures) != 1:
        raise ValueError("项目需要包含且只包含一个服装骨架")
    armature = armatures[0]
    armature["csw_baseline"] = json.dumps(baseline)
    armature["csw_source_file"] = str(filepath)
    armature["csw_return_file"] = "BlenderReturn/edited.gltf"
    if descriptor_path.is_file():
        descriptor = json.loads(descriptor_path.read_text(encoding="utf-8-sig"))
        if descriptor.get("available") and descriptor.get("projectId") != baseline["projectId"]:
            raise ValueError("人体挂接信息不属于当前项目，请重新创建项目")
        armature["csw_anchor_rig_info"] = json.dumps(descriptor)
    bpy.context.scene["csw_active_project_id"] = baseline["projectId"]
    originals = {bone["name"]: bone for bone in baseline["bones"]}
    if set(originals) != set(armature.data.bones.keys()):
        raise ValueError("导入时未保留全部原骨名称，请重新打开项目")
    for bone in armature.data.bones:
        original = originals[bone.name]
        bone["cswBoneId"] = original["id"]
        bone["cswOriginalIndex"] = original["originalIndex"]
        bone["cswOriginalName"] = original["name"]
        bone["cswImportMatrix"] = [value for row in bone.matrix_local for value in row]
    for pose_bone in armature.pose.bones:
        pose_bone.custom_shape = None
    for obj in created:
        if obj.type == "MESH":
            obj["csw_proxy"] = True
            obj.display_type = "WIRE"
            obj.hide_render = True
    # Body bones are authoring helpers in the same editable rig. The compiler
    # retains only used anchors and remaps the equipment file on return.
    reference_path = filepath.with_name("reference.gltf")
    if reference_path.is_file():
        before_reference = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=str(reference_path), bone_heuristic="BLENDER", guess_original_bind_pose=False)
        reference_objects = set(bpy.data.objects) - before_reference
        if descriptor_path.is_file() and descriptor.get("available"):
            anchors.add_reference(armature, reference_objects)
            reference_objects = []
        for obj in reference_objects:
            obj["csw_reference"] = True
            obj.hide_render = True
            if obj.type == "ARMATURE":
                obj.name = "基础骨架_仅供参考"
                obj.show_in_front = True
                obj.display_type = "WIRE"
                for pose_bone in obj.pose.bones:
                    pose_bone.custom_shape = None
            else:
                obj.hide_set(True)
            obj.select_set(False)
            obj.hide_select = True
    armature.name = "服装骨架_编辑此对象"
    armature.show_in_front = True
    bpy.ops.object.select_all(action="DESELECT")
    armature.select_set(True)
    bpy.context.view_layer.objects.active = armature
    return armature


def add_test_bone(armature, name="csw_test_bone", parent_name=None):
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    bpy.context.view_layer.objects.active = armature
    armature.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    parent = armature.data.edit_bones.get(parent_name) if parent_name else armature.data.edit_bones[0]
    if parent is None:
        raise ValueError("找不到所选父骨，请重新选择服装骨架中的骨骼")
    bone = armature.data.edit_bones.new(name)
    bone.head = parent.head.copy() if parent.name in {b.name for b in armature.data.bones if anchors.is_anchor(b)} else parent.tail.copy()
    bone.tail = bone.head + Vector((0, 0.05, 0))
    bone.parent = parent
    created_name = bone.name
    bpy.ops.object.mode_set(mode="OBJECT")
    armature.data.bones[created_name]["cswBoneId"] = str(uuid.uuid4())
    armature.data.bones[created_name]["cswOriginalIndex"] = -1
    return created_name


def add_test_chain(armature, parent_name=None):
    """Three visible segments plus an explicit terminal joint for SKLB viewers."""
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    bpy.context.view_layer.objects.active = armature
    armature.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    try:
        anchor = armature.data.edit_bones.get(parent_name) if parent_name else armature.data.edit_bones[0]
        if anchor is None:
            raise ValueError("找不到骨链父骨，请重新选择服装骨架中的骨骼")
        origin = anchor.head.copy()
        step = anchor.matrix.to_3x3() @ Vector((0, 0.04, 0))
        names = []
        parent = anchor
        for index, suffix in enumerate(("00", "01", "02", "tip")):
            bone = armature.data.edit_bones.new("csw_chain_" + suffix)
            bone.head = origin + step * index
            bone.tail = origin + step * (index + 1)
            bone.parent = parent
            bone.use_connect = index > 0
            names.append(bone.name)
            parent = bone
    finally:
        bpy.ops.object.mode_set(mode="OBJECT")
    for name in names:
        bone = armature.data.bones[name]
        bone["cswBoneId"] = str(uuid.uuid4())
        bone["cswOriginalIndex"] = -1
    return names


def export_project(armature, filepath=None):
    target = return_path(armature)
    if filepath is not None and Path(filepath).resolve().with_suffix(".gltf") != target:
        raise ValueError("返回位置由插件固定管理，请使用“返回游戏”")
    target.parent.mkdir(parents=True, exist_ok=True)
    # Publish a complete export only after validation and Blender export succeed.
    with tempfile.TemporaryDirectory(prefix=".export-", dir=target.parent) as directory:
        staged = Path(directory) / target.name
        _export_project(armature, staged)
        for suffix in (".bin", ".ffxiv.json", ".gltf", ".complete.json"):
            source = staged.with_suffix(suffix)
            if source.is_file():
                # Inherit the return folder's permissions. A secure temporary directory
                # can otherwise make a moved file unreadable to the game process.
                published = target.parent / (".publish-" + uuid.uuid4().hex + suffix)
                try:
                    with published.open("xb") as stream:
                        stream.write(source.read_bytes())
                    os.replace(published, target.with_suffix(suffix))
                finally:
                    published.unlink(missing_ok=True)
    return target


def _export_project(armature, filepath):
    if not armature or armature.type != "ARMATURE" or "csw_baseline" not in armature:
        raise ValueError("请先打开服装骨架项目")
    if armature.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    baseline = json.loads(armature["csw_baseline"])
    bpy.context.view_layer.update()
    expected_world = Matrix.Identity(4)
    if any(abs(armature.matrix_world[row][col] - expected_world[row][col]) > 1e-5 for row in range(4) for col in range(4)):
        raise ValueError("请保持骨架对象变换为初始值，使用编辑模式调整新骨，并直接给新骨指定父级；旧版整套换挂点场景请重新创建")
    expected = {bone["id"]: bone for bone in baseline["bones"]}
    identities = {}
    for bone in armature.data.bones:
        identity = bone.get("cswBoneId")
        if not identity:
            identity = str(uuid.uuid4())
            bone["cswBoneId"] = identity
            bone["cswOriginalIndex"] = -1
        uuid.UUID(identity)
        if identity in identities:
            raise ValueError("骨骼身份重复。请撤销原骨复制，使用“添加服装骨骼”创建新骨")
        identities[identity] = bone
        if identity in expected:
            original = expected[identity]
            if bone.name != original["name"]:
                raise ValueError(f"原骨已被改名，请恢复原名：{original['name']}")
            parent_id = bone.parent.get("cswBoneId") if bone.parent else None
            if parent_id != original["parentId"]:
                raise ValueError(f"原骨父级发生变化，请恢复：{bone.name}")
            imported = bone.get("cswImportMatrix")
            current = [value for row in bone.matrix_local for value in row]
            if imported is None or any(abs(a-b) > 1e-5 for a,b in zip(imported,current)):
                raise ValueError(f"原骨静止姿态发生变化，请撤销对原骨的编辑：{bone.name}")
    missing = set(expected) - set(identities)
    if missing:
        raise ValueError("原有骨骼被删除，请恢复原骨")
    filepath = Path(filepath).resolve().with_suffix(".gltf")
    filepath.parent.mkdir(parents=True, exist_ok=True)
    proxies = [obj for obj in bpy.data.objects if obj.get("csw_proxy") and any(mod.type == "ARMATURE" and mod.object == armature for mod in obj.modifiers)]
    if not proxies:
        raise ValueError("骨架辅助网格被删除，请重新打开项目")
    with anchors.export_rig(armature, proxies) as (export_armature, retained):
        export_names = set(export_armature.data.bones.keys())
        bpy.ops.export_scene.gltf(filepath=str(filepath), export_format="GLTF_SEPARATE", use_selection=True,
                                  export_extras=True, export_animations=False, export_def_bones=False,
                                  export_skins=True, export_yup=True, export_materials="NONE")
    gltf = json.loads(filepath.read_text(encoding="utf-8"))
    # Do not export Blender-only properties such as local source filenames.
    def clean_extras(value):
        if isinstance(value, dict):
            if "extras" in value:
                safe = {key: item for key, item in value["extras"].items()
                        if key in {"cswBoneId", "cswProjectId", "cswSourceSha256"}}
                if safe:
                    value["extras"] = safe
                else:
                    del value["extras"]
            for child in value.values():
                clean_extras(child)
        elif isinstance(value, list):
            for child in value:
                clean_extras(child)
    clean_extras(gltf)
    if len(gltf.get("skins", [])) != 1:
        raise ValueError("导出结果应只包含一个服装骨架，请重新打开项目")
    nodes = gltf["nodes"]
    bone_nodes = {}
    for index, node in enumerate(nodes):
        name = node.get("name")
        if name in export_names:
            if name in bone_nodes:
                raise ValueError("导出存在重复名称：" + name)
            bone_nodes[name] = index
            node.setdefault("extras", {})["cswBoneId"] = armature.data.bones[name]["cswBoneId"]
    if set(bone_nodes) != export_names:
        raise ValueError("导出时丢失骨骼，请保留辅助网格并重新打开项目")
    joints = set(gltf["skins"][0]["joints"])
    if not set(bone_nodes.values()).issubset(joints):
        raise ValueError("导出时丢失未加权骨骼，请保留辅助网格并重试")
    gltf.setdefault("extras", {}).update(cswProjectId=baseline["projectId"], cswSourceSha256=baseline["sourceSha256"])
    anchors.finish_gltf(armature, gltf, retained)
    filepath.write_text(json.dumps(gltf, ensure_ascii=False, indent=2), encoding="utf-8")
    filepath.with_suffix(".ffxiv.json").write_text(json.dumps(baseline, ensure_ascii=False, indent=2), encoding="utf-8")
    manifest = {"projectId": baseline["projectId"], "gltfSha256": hashlib.sha256(filepath.read_bytes()).hexdigest(), "boneCount": len(export_names)}
    filepath.with_suffix(".complete.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    return filepath


def current_armature(context):
    obj = context.object
    if obj and obj.type == "ARMATURE" and "csw_baseline" in obj:
        return obj
    active_id = context.scene.get("csw_active_project_id")
    candidates = [obj for obj in context.scene.objects if obj.type == "ARMATURE" and "csw_baseline" in obj]
    if active_id:
        candidates = [obj for obj in candidates if json.loads(obj["csw_baseline"])["projectId"] == active_id]
    return candidates[0] if len(candidates) == 1 else None


def return_path(armature):
    if not armature or "csw_source_file" not in armature:
        raise ValueError("请先导入服装骨架项目")
    return _return_file_for_source(armature["csw_source_file"])


def focus_new_bones(armature, names):
    bpy.context.view_layer.objects.active = armature
    bpy.ops.object.mode_set(mode="EDIT")
    for bone in armature.data.edit_bones:
        bone.select = bone.select_head = bone.select_tail = bone.name in names
    armature.data.edit_bones.active = armature.data.edit_bones[names[0]]


class CSW_OT_import(bpy.types.Operator, ImportHelper):
    bl_idname = "csw.import_project"
    bl_label = "导入服装骨架项目"
    filename_ext = ".gltf"
    filter_glob: StringProperty(default="*.gltf", options={"HIDDEN"})

    def execute(self, context):
        try:
            import_project(self.filepath)
            return {"FINISHED"}
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}


class CSW_OT_return(bpy.types.Operator):
    bl_idname = "csw.return_to_game"
    bl_label = "返回游戏"
    bl_description = "导出到插件目录的固定返回位置，游戏插件会自动识别，无需选择路径"

    def execute(self, context):
        try:
            armature = current_armature(context)
            if not armature:
                raise ValueError("请先导入服装骨架项目")
            export_project(armature)
            self.report({"INFO"}, "已返回游戏，切回游戏点击“回导并检查”")
            return {"FINISHED"}
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}


class CSW_OT_add_bone(bpy.types.Operator):
    bl_idname = "csw.add_bone"
    bl_label = "添加服装骨骼"

    def execute(self, context):
        armature = current_armature(context)
        if not armature or armature.type != "ARMATURE" or "csw_baseline" not in armature:
            self.report({"ERROR"}, "请先导入服装骨架项目")
            return {"CANCELLED"}
        selected = context.active_bone.name if context.object == armature and context.active_bone else None
        try:
            name = add_test_bone(armature, "csw_bone", selected)
            focus_new_bones(armature, [name])
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        return {"FINISHED"}


class CSW_PT_panel(bpy.types.Panel):
    bl_label = "服装骨架"
    bl_idname = "CSW_PT_panel"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "服装骨架"

    def draw(self, context):
        armature = current_armature(context)
        if not armature:
            self.layout.label(text="先在游戏中创建项目并打开 Blender")
            self.layout.operator("csw.import_project", text="手动打开项目…")
            return
        baseline = json.loads(armature["csw_baseline"])
        helpers = [bone for bone in armature.data.bones if anchors.is_anchor(bone)]
        self.layout.label(text=f"原骨 {len(baseline['bones'])} · 新增 {len(anchors.authored(armature))}")
        self.layout.label(text=f"可选人体挂点 {armature.get('csw_anchor_display_count', 0)} · 已用 {len(anchors.used(armature))}")
        character = (anchors.info(armature) or {}).get("characterSource", {})
        if character:
            self.layout.label(text="人物来源：" + ("自己" if character.get("selectionKind") == "self" else "当前目标") + " · " + character.get("displayName", ""))
        self.layout.label(text="编辑模式选择挂点后添加骨链")
        self.layout.label(text="已有新链：选根骨，再选挂点")
        self.layout.label(text="Ctrl+P → 保持偏移，手动指定父级")
        self.layout.label(text="未使用的挂点在返回时自动忽略")
        if not helpers:
            self.layout.label(text="完整挂点不可用时，请在新版插件新建项目")
        self.layout.operator("csw.add_bone", icon="BONE_DATA")
        self.layout.operator("csw.add_chain", icon="CONSTRAINT_BONE")
        self.layout.separator()
        row = self.layout.row()
        row.scale_y = 1.6
        row.operator("csw.return_to_game", icon="EXPORT")
        self.layout.label(text="返回位置已关联，导出后切回游戏即可")
        self.layout.label(text="人体挂点可直接选作新链父级")
        advanced = self.layout.box()
        advanced.label(text="其他文件操作")
        advanced.operator("csw.import_project", text="打开其他项目…")


class CSW_OT_add_chain(bpy.types.Operator):
    bl_idname = "csw.add_chain"
    bl_label = "添加三段骨链"

    def execute(self, context):
        armature = current_armature(context)
        if not armature or armature.type != "ARMATURE" or "csw_baseline" not in armature:
            self.report({"ERROR"}, "请先导入服装骨架项目")
            return {"CANCELLED"}
        selected = context.active_bone.name if context.object == armature and context.active_bone else None
        try:
            names = add_test_chain(armature, selected)
            focus_new_bones(armature, names)
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        return {"FINISHED"}


CLASSES = (CSW_OT_import, CSW_OT_return, CSW_OT_add_bone, CSW_OT_add_chain, CSW_PT_panel)


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)


def unregister():
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)

