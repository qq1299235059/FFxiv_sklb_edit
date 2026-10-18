"""Real Blender multi-part authoring and return regression."""
import json, shutil, sys
from pathlib import Path
import bpy
source, addon, output = [Path(p).resolve() for p in sys.argv[sys.argv.index('--')+1:]]
output.mkdir(parents=True,exist_ok=True)
fixture=output/'source';fixture.mkdir(exist_ok=True)
for name in ('skeleton.gltf','skeleton.ffxiv.json','skeleton.complete.json','skeleton.bin','reference.gltf','reference.ffxiv.json','reference.complete.json','reference.bin','anchor-rig.json'):
    if (source/name).is_file():shutil.copy2(source/name,fixture/name)
(fixture/'.csw-network.json').write_text('{}')
if addon.suffix == '.zip':
    assert bpy.ops.preferences.addon_install(filepath=str(addon),overwrite=True)=={'FINISHED'}
    assert bpy.ops.preferences.addon_enable(module='clothing_skeleton_bridge')=={'FINISHED'}
    bridge=sys.modules['clothing_skeleton_bridge']
else:
    sys.path.insert(0,str(addon.parent));import clothing_skeleton_bridge as bridge
    bridge.register()
bridge.connection.bridge.stop()
# Produce two independent model fixtures with actual skin weights, UVs and materials.
before=set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=str(fixture/'reference.gltf'),bone_heuristic='BLENDER',guess_original_bind_pose=False)
rig=next(obj for obj in set(bpy.data.objects)-before if obj.type=='ARMATURE')
meshdata=bpy.data.meshes.new('Garment');meshdata.from_pydata([(0,0,0),(.1,0,0),(0,.1,0)],[],[(0,1,2)]);meshdata.uv_layers.new()
mesh=bpy.data.objects.new('Garment',meshdata);bpy.context.scene.collection.objects.link(mesh)
meshdata.materials.append(bpy.data.materials.new('Material'))
mesh.vertex_groups.new(name='j_sebo_c').add([0,1,2],1,'REPLACE')
modifier=mesh.modifiers.new('Armature','ARMATURE');modifier.object=rig
mesh.parent=rig
for obj in list(set(bpy.data.objects)-before):
    if obj.type=='MESH' and obj!=mesh:bpy.data.objects.remove(obj,do_unlink=True)
bpy.ops.object.select_all(action='DESELECT');rig.select_set(True);mesh.select_set(True);bpy.context.view_layer.objects.active=rig
name='ref-'+'a'*16+'.glb'
bpy.ops.export_scene.gltf(filepath=str(fixture/name),export_format='GLB',use_selection=True,export_animations=False,export_def_bones=False)
for obj in set(bpy.data.objects)-before:bpy.data.objects.remove(obj,do_unlink=True)
identity=json.loads((fixture/'skeleton.ffxiv.json').read_text(encoding='utf-8-sig'))['projectId']
modid='eb2282c1-9bd5-4241-87bd-6edfa3de778d'
paths=['chara/equipment/e0100/model/c0201e0100_top.mdl','chara/equipment/e0100/model/c0201e0100_glv.mdl']
(fixture/'mod-project.json').write_text(json.dumps(dict(id=modid,name='Multi part test',targetModelGamePath=paths[0],exportClothing=True)))
(fixture/'models.json').write_text(json.dumps(dict(projectId=identity,modId=modid,models=[dict(id=str(i),group='clothing',slot=slot,label=slot,gamePath=p,file=name,editable=True) for i,(slot,p) in enumerate(zip(['top','glv'],paths))])))
cube=bpy.data.objects.get('Cube')
primary=bridge.import_project(fixture/'skeleton.gltf');primary['csw_network_revision']='f'*64
parts=bridge.assets.parts(primary)
assert len(parts)==2 and parts[0].data!=parts[1].data
assert parts[0].users_collection!=parts[1].users_collection
assert cube is None or bpy.data.objects.get('Cube')==cube
assert not bridge.assets.check(primary),bridge.assets.check(primary)
for part in parts:
    meshes=bridge.assets.garment_meshes(primary,part);assert len(meshes)==1
    assert next(m.object for m in meshes[0].modifiers if m.type=='ARMATURE')==part
# Add an entire chain to the separate glove rig.
aux=parts[1];names=bridge.add_test_chain(aux,'j_te_r')
bridge.assets.sync(primary)
assert len(bridge.anchors.authored(aux))==4
assert not bridge.assets.check(primary),bridge.assets.check(primary)
original_objects=set(bpy.data.objects)
with bridge.assets.merged_skeleton(primary) as merged:
    assert merged!=primary and all(n in merged.data.bones for n in names)
    result=bridge.export_project(merged)
assert set(bpy.data.objects)==original_objects and all(n not in primary.data.bones for n in names)
assert all(n in aux.data.bones for n in names)
assert len(json.loads(result.read_text())['skins'][0]['joints'])==15
items=bridge.assets.export_garments(primary,result.parent)
assert len(items)==2
for item in items:
    document=json.loads((result.parent/item['file']).read_text());assert len(document['skins'])==1
    assert all(b['uri'].startswith('data:') for b in document.get('buffers',[]))
bridge.connection.bridge.connected=True
bridge.connection.bridge.enqueue_return(primary,result,items)
packet=bridge.connection.bridge.outgoing.get_nowait()[1]
assert len(packet['files'])==6 and packet['modId']==modid
bridge.connection.bridge.enqueue_return(primary,result)
packet=bridge.connection.bridge.outgoing.get_nowait()[1];assert len(packet['files'])==4
# Detect accidental body-collection placement on an auxiliary rig.
bone=aux.data.bones[names[0]];bridge.anchors.bone_collections(aux)['body'].assign(bone)
assert bridge.assets.check(primary)
assert bpy.ops.csw.arrange_custom()=={'FINISHED'}
assert not bridge.assets.check(primary),bridge.assets.check(primary)
# Model-only mode protects the original body and returns it without an SKLB contract.
bodyfixture=output/'body-only';bodyfixture.mkdir(exist_ok=True)
for prefix in ('skeleton','reference'):
    for extension in ('gltf','ffxiv.json','complete.json'):
        shutil.copy2(fixture/('reference.'+extension),bodyfixture/(prefix+'.'+extension))
    if (fixture/'reference.bin').is_file():shutil.copy2(fixture/'reference.bin',bodyfixture/(prefix+'.bin'))
bodydoc=json.loads((fixture/'reference.ffxiv.json').read_text(encoding='utf-8-sig'))
descriptor=json.loads((fixture/'anchor-rig.json').read_text(encoding='utf-8-sig'))
descriptor.update(projectId=bodydoc['projectId'],sourceGamePath=bodydoc['sourceGamePath'],sourceSha256=bodydoc['sourceSha256'],targets=[],existingAnchors=[],allowNewBones=False)
(bodyfixture/'anchor-rig.json').write_text(json.dumps(descriptor))
(bodyfixture/'.csw-network.json').write_text('{}')
body=bridge.import_project(bodyfixture/'skeleton.gltf');body['csw_network_revision']='e'*64
assert not bridge.anchors.check_status(body)['errors'],bridge.anchors.check_status(body)
with bridge.assets.merged_skeleton(body) as merged:bodyreturn=bridge.export_project(merged)
assert len(json.loads(bodyreturn.read_text())['skins'][0]['joints'])==len(bodydoc['bones'])
bridge.add_test_bone(body,'csw_forbidden','j_te_l');bridge.anchors.sync_collections(body)
try:
    with bridge.assets.merged_skeleton(body):pass
except ValueError as e:assert '附加骨架' in str(e)
else:raise AssertionError('Model-only project must reject new bones')
(output/'report.json').write_text(json.dumps(dict(state='passed',parts=2,separateCollections=True,separateArmatures=True,auxiliaryChainMergedOnlyOnExport=True,fullAndSkeletonOnly=True,unrelatedScenePreserved=True),indent=2))
if addon.suffix == '.zip':bpy.ops.preferences.addon_disable(module='clothing_skeleton_bridge')
else:bridge.unregister()
print('CSW_MOD_PARTS_PASSED',flush=True)
