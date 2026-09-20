from __future__ import annotations

from pathlib import Path

import bpy
from mathutils import Vector


ROOT = Path(__file__).resolve().parents[1]


def point_at(obj, target) -> None:
    obj.rotation_euler = (Vector(target) - obj.location).to_track_quat("-Z", "Y").to_euler()


for collection in bpy.data.collections:
    if collection.name.startswith("CS1_ROAD_"):
        visible = collection.name == "CS1_ROAD_elevated"
        collection.hide_viewport = not visible
        collection.hide_render = not visible

scene = bpy.context.scene
scene.render.engine = "BLENDER_WORKBENCH"
scene.display.shading.light = "STUDIO"
scene.display.shading.color_type = "MATERIAL"
scene.display.shading.show_shadows = True
scene.display.shading.show_cavity = True
scene.display.shading.cavity_type = "WORLD"
scene.render.resolution_x = 1100
scene.render.resolution_y = 700
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = "PNG"
scene.render.film_transparent = False

camera_data = bpy.data.cameras.new("Geometry Preview Camera")
camera = bpy.data.objects.new("Geometry Preview Camera", camera_data)
scene.collection.objects.link(camera)
camera.location = (24.0, -82.0, 1.5)
camera_data.lens = 58.0
point_at(camera, (0.0, -42.0, 6.4))
scene.camera = camera

output = ROOT / "build" / "smoke" / "elevated-girders.png"
output.parent.mkdir(parents=True, exist_ok=True)
scene.render.filepath = str(output)
bpy.ops.render.render(write_still=True)
print(f"GEOMETRY_PREVIEW_OK {output}")
