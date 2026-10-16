"""Authoring JSON and runtime mesh material must use the same output policy."""
import importlib
import json
from pathlib import Path
import sys

import bpy

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT / "blender_addon"))
builder = importlib.import_module("road_builder")
builder.register()
builder._initialize_scene_lanes()
fixture = ROOT / "build/smoke/texture-fixture/dimensions.json"
builder.DEFAULT_TEXTURE_LAYOUT = fixture
builder._TEXTURE_LAYOUT_CACHE = None
objects = builder.build_mode(bpy.context.scene,"basic")
output = ROOT / "build/smoke/output-contract.json"
assert bpy.ops.cs1_road.export_spec(filepath=str(output)) == {"FINISHED"}
authoring = json.loads(output.read_text(encoding="utf-8"))
for mode in ("basic","elevated","bridge","slope","tunnel"):
    serialized = builder._runtime_export.serialize_network_modes({mode:objects},{},{})
    for entry in serialized[0]["entries"]:
        assert entry["mesh"]["material"]["shader"] == authoring["modes"][mode]["shader"] == "Custom/Net/RoadBridge"
        assert entry["mesh"]["material"]["main_texture_scale"] == authoring["modes"][mode]["main_texture_scale"] == [1.0,0.5]
print("OUTPUT_CONTRACT_SMOKE_OK authoring_runtime_shader_scale modes=5")
