"""Open one CSW project in a fresh Blender process with a session-only bridge."""
import importlib.util
import json
from pathlib import Path
import re
import sys

import bpy

root = Path(__file__).resolve().parents[1]
project = Path(sys.argv[sys.argv.index("--") + 1]).resolve()
receipt = root / "BlenderReturn" / "blender-launch.json"
receipt.parent.mkdir(parents=True, exist_ok=True)
project_id = json.loads(project.with_suffix(".ffxiv.json").read_text(encoding="utf-8-sig"))["projectId"]


def open_project():
    try:
        # This script is launched only in a new --factory-startup process.
        # Resetting factory settings here would invalidate Blender's GUI context.
        for obj in list(bpy.data.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        spec = importlib.util.spec_from_file_location("clothing_skeleton_bridge", Path(__file__).parent / "clothing_skeleton_bridge/__init__.py")
        bridge = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = bridge
        spec.loader.exec_module(bridge)
        bridge.configure_plugin_root(root)
        bridge.register()
        armature = bridge.import_project(project)
        for screen in bpy.data.screens:
            for area in screen.areas:
                if area.type == "VIEW_3D":
                    area.spaces.active.show_region_ui = True
                    area.spaces.active.overlay.show_relationship_lines = True
                    view = area.spaces.active.region_3d
                    view.view_location = armature.matrix_world @ armature.data.bones[0].head_local
                    view.view_distance = 2.4
        receipt.write_text(json.dumps(dict(state="opened", projectId=project_id), ensure_ascii=False), encoding="utf-8")
        print("CSW_PROJECT_OPENED", flush=True)
    except Exception as error:
        detail=str(error).replace(str(root),"<���Ŀ¼>")
        detail=re.sub(r"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n\t\"<>]+","<����·��>",detail)
        receipt.write_text(json.dumps(dict(state="failed", projectId=project_id, error=detail), ensure_ascii=False), encoding="utf-8")
        raise


if bpy.app.background:
    open_project()
else:
    bpy.app.timers.register(open_project, first_interval=0.5)
