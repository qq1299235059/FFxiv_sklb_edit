"""Round-trip actual codec GLB through Blender and produce a self-contained glTF."""
from pathlib import Path
import sys,json,bpy
source,addon,output=[Path(p).resolve() for p in sys.argv[sys.argv.index('--')+1:]]
sys.path.insert(0,str(addon.parent));from clothing_skeleton_bridge.assets import _clean,_embed
bpy.ops.object.select_all(action='SELECT');bpy.ops.object.delete(use_global=False)
bpy.ops.import_scene.gltf(filepath=str(source),bone_heuristic='TEMPERANCE',guess_original_bind_pose=False)
for rig in (o for o in bpy.context.scene.objects if o.type=='ARMATURE'):
    for bone in rig.pose.bones:bone.custom_shape=None
output.parent.mkdir(parents=True,exist_ok=True)
bpy.ops.export_scene.gltf(filepath=str(output),export_format='GLTF_SEPARATE',export_animations=False,export_extras=True,export_attributes=True,export_try_sparse_sk=False,export_tangents=True,export_def_bones=False)
gltf=json.loads(output.read_text());_clean(gltf);_embed(gltf,output.parent);output.write_text(json.dumps(gltf),encoding='utf-8')
print('CSW_MODEL_ROUNDTRIP_BLENDER_PASSED',flush=True)
