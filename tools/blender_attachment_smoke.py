"""Compatibility entry point for the complete authoring anchor workflow."""
from pathlib import Path
import runpy
runpy.run_path(str(Path(__file__).with_name("blender_anchor_rig_smoke.py")),run_name="__main__")
