"""Headless client for a captured real per-Mod exchange (no game UI or user scene)."""
import json,sys,time
from pathlib import Path
import bpy
addon,port,output=[v for v in sys.argv[sys.argv.index('--')+1:]][:3];addon=Path(addon).resolve();output=Path(output).resolve();output.mkdir(parents=True,exist_ok=True)
sys.path.insert(0,str(addon.parent));import clothing_skeleton_bridge as bridge
bridge.register();bridge.connection.bridge.start(int(port),output/'cache');end=time.monotonic()+40
while time.monotonic()<end:
    bridge.connection.bridge.tick(bpy,bridge.import_project)
    primary=bridge.current_armature(bpy.context)
    if primary and primary.get('csw_network_revision'):break
    time.sleep(.1)
else:raise RuntimeError(bridge.connection.bridge.message)
parts=bridge.assets.parts(primary);assert len(parts)>=2 and len({p.data for p in parts})==len(parts)
assert len({p.users_collection[0] for p in parts})==len(parts)
assert not bridge.assets.check(primary),bridge.assets.check(primary)
if '--add-bones' in sys.argv:
    auxiliary=next(p for p in parts if p.get('csw_part_label')=='手套')
    bridge.add_test_chain(auxiliary,'j_te_r');bridge.assets.sync(primary)
    # Give one glove vertex a real custom-bone weight for MDL validation.
    mesh=bridge.assets.garment_meshes(primary,auxiliary)[0]
    mesh.vertex_groups.new(name='csw_chain_00').add([0],1,'REPLACE')
target=bridge.return_path(primary);target.parent.mkdir(parents=True,exist_ok=True)
items=bridge.assets.export_garments(primary,target.parent)
with bridge.assets.merged_skeleton(primary) as merged:bridge.export_project(merged)
bridge.connection.bridge.enqueue_return(primary,target,items)
end=time.monotonic()+25
while time.monotonic()<end:
    if bridge.connection.bridge.message.startswith('已返回'):break
    time.sleep(.15)
else:raise RuntimeError(bridge.connection.bridge.message)
(output/'report.json').write_text(json.dumps(dict(state='passed',partCount=len(parts),returnedPartCount=len(items),separateArmatures=True,separateCollections=True,hasCustomBones='--add-bones' in sys.argv,groups=[c.name for c in bpy.data.collections if c.name.startswith('参考 ·')]),ensure_ascii=False,indent=2),encoding='utf-8')
bridge.unregister();print('CSW_REAL_MOD_RETURN_PASSED',flush=True)
