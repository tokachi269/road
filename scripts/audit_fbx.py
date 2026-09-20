from __future__ import annotations

import json
import sys
from pathlib import Path

import bpy
from mathutils import Vector


def reset_scene() -> None:
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for mesh in list(bpy.data.meshes):
        bpy.data.meshes.remove(mesh)


def audit(path: Path) -> dict[str, object]:
    reset_scene()
    bpy.ops.wm.fbx_import(filepath=str(path))
    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    points = [obj.matrix_world @ Vector(corner) for obj in meshes for corner in obj.bound_box]
    bounds = {
        axis: [min(point[index] for point in points), max(point[index] for point in points)]
        for index, axis in enumerate("xyz")
    }
    axis_values = {
        axis: sorted({round(float((obj.matrix_world @ vertex.co)[index]), 4) for obj in meshes for vertex in obj.data.vertices})
        for index, axis in enumerate("xyz")
    }
    return {
        "file": path.name,
        "objects": len(meshes),
        "vertices": sum(len(obj.data.vertices) for obj in meshes),
        "polygons": sum(len(obj.data.polygons) for obj in meshes),
        "uv_layers": sum(len(obj.data.uv_layers) for obj in meshes),
        "material_slots": sum(len(obj.material_slots) for obj in meshes),
        "bounds": bounds,
        "dimensions": {axis: limits[1] - limits[0] for axis, limits in bounds.items()},
        "axis_value_counts": {axis: len(values) for axis, values in axis_values.items()},
        "y_values": axis_values["y"] if len(axis_values["y"]) <= 80 else axis_values["y"][:40] + ["..."] + axis_values["y"][-40:],
    }


args = sys.argv[sys.argv.index("--") + 1 :]
results = [audit(Path(value)) for value in args]
print("AUDIT_JSON=" + json.dumps(results, ensure_ascii=False))
