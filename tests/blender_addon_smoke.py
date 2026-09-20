from __future__ import annotations

import importlib
import json
import sys
from pathlib import Path

import bpy


ROOT = Path(__file__).resolve().parents[1]
ADDON_PARENT = ROOT / "blender_addon"
sys.path.insert(0, str(ADDON_PARENT))

road_builder = importlib.import_module("road_builder")
road_builder.register()


class FakeLayout:
    """Minimal Blender UILayout stand-in that exercises the panel draw code."""

    layout_type = "DEFAULT"

    def __getattr__(self, name):
        def method(*args, **kwargs):
            return self

        return method

props = bpy.context.scene.cs1_road_builder
props.road_name = "Addon Smoke Road"
props.lanes.clear()
for index, direction in enumerate(("BACKWARD", "BACKWARD", "FORWARD", "FORWARD")):
    lane = props.lanes.add()
    lane.name = f"Test lane {index + 1}"
    lane.zone = "ROAD"
    lane.width = 3.25
    lane.direction = direction
    lane.lane_type = "VEHICLE"
    lane.vehicle_type = "CAR"
props.shoulder_width = 1.0
props.sidewalk_width = 3.0
props.curb_height = 0.2
props.surface_profile = "DEPRESSED"
props.node_transition_target = "FLUSH"
props.node_shoulder_bands = False

result = bpy.ops.cs1_road.build_all()
assert result == {"FINISHED"}

for mode in ("basic", "elevated", "bridge", "slope", "tunnel"):
    collection = bpy.data.collections.get(f"CS1_ROAD_{mode}")
    assert collection is not None, mode
    assert len(collection.objects) == 2, (mode, [obj.name for obj in collection.objects])
    assert collection.objects.get(f"{mode}_segment") is not None, mode
    assert collection.objects.get(f"{mode}_node") is not None, mode
    for obj in collection.objects:
        assert len(obj.data.uv_layers) == 1, obj.name
        for uv in (loop.uv for loop in obj.data.uv_layers[0].data):
            assert -1e-6 <= uv.x <= 1.0 + 1e-6, (obj.name, uv[:])
            assert -1e-6 <= uv.y <= 1.0 + 1e-6, (obj.name, uv[:])

left_node = bpy.data.objects["basic_node"]
node_top_z = sorted({round(vertex.co.z, 4) for vertex in left_node.data.vertices})
assert -0.2 in node_top_z and 0.0 in node_top_z, node_top_z
assert left_node["cs1_center_split"] is True
assert left_node.location.y == 32.0

basic_segment = bpy.data.objects["basic_segment"]
assert len(basic_segment.data.uv_layers) == 1
assert sum(polygon.material_index == 1 for polygon in basic_segment.data.polygons) == 100
assert sum(polygon.material_index == 1 for polygon in left_node.data.polygons) == 0
assert len(left_node.data.materials) == 1
assert basic_segment.location.y == -32.0
assert basic_segment["cs1_longitudinal_slices"] == 20
segment_y = [vertex.co.y for vertex in basic_segment.data.vertices]
assert round(max(segment_y) - min(segment_y), 4) == 64.0, (min(segment_y), max(segment_y))

node_y = [vertex.co.y for vertex in left_node.data.vertices]
assert round(max(node_y) - min(node_y), 4) == 64.0, (min(node_y), max(node_y))
assert len({round(value, 4) for value in segment_y}) == 21
assert len({round(value, 4) for value in node_y}) == 9
assert left_node["cs1_longitudinal_slices"] == 8

segment_x = {round(vertex.co.x, 4) for vertex in basic_segment.data.vertices}
for marking_center in (-6.5, -3.25, 0.0, 3.25, 6.5):
    assert round(marking_center, 4) not in segment_x, (marking_center, sorted(segment_x))
    assert round(marking_center - 0.2, 4) in segment_x
    assert round(marking_center + 0.2, 4) in segment_x
node_x = {round(vertex.co.x, 4) for vertex in left_node.data.vertices}
assert node_x == {-10.5, -7.5, 0.0, 7.5, 10.5}, node_x


def uvs_at_coordinate(obj, coordinate):
    vertex_indices = {
        vertex.index for vertex in obj.data.vertices
        if all(abs(vertex.co[index] - coordinate[index]) < 1e-6 for index in range(3))
    }
    layer = obj.data.uv_layers[0]
    return {
        (round(layer.data[loop.index].uv.x, 6), round(layer.data[loop.index].uv.y, 6))
        for loop in obj.data.loops if loop.vertex_index in vertex_indices
    }


left_curb_top_uv = uvs_at_coordinate(basic_segment, (-7.5, -32.0, 0.0))
left_curb_bottom_uv = uvs_at_coordinate(basic_segment, (-7.5, -32.0, -0.2))
right_curb_bottom_uv = uvs_at_coordinate(basic_segment, (7.5, -32.0, -0.2))
right_curb_top_uv = uvs_at_coordinate(basic_segment, (7.5, -32.0, 0.0))
assert len(left_curb_top_uv) == len(left_curb_bottom_uv) == 1
assert len(right_curb_top_uv) == len(right_curb_bottom_uv) == 1
left_top_u = next(iter(left_curb_top_uv))[0]
left_bottom_u = next(iter(left_curb_bottom_uv))[0]
right_bottom_u = next(iter(right_curb_bottom_uv))[0]
right_top_u = next(iter(right_curb_top_uv))[0]
expected_curb_uv_width = 0.2 / 21.4
assert abs((left_bottom_u - left_top_u) - expected_curb_uv_width) < 1e-5
assert abs((right_top_u - right_bottom_u) - expected_curb_uv_width) < 1e-5

uv_layer = basic_segment.data.uv_layers[0]
for uv in (loop.uv for loop in uv_layer.data):
    assert -1e-6 <= uv.x <= 1.0 + 1e-6, uv[:]
    assert -1e-6 <= uv.y <= 1.0 + 1e-6, uv[:]
marking_uv = []
for polygon in (item for item in basic_segment.data.polygons if item.material_index == 1):
    face_uv = [uv_layer.data[index].uv for index in polygon.loop_indices]
    marking_uv.extend(face_uv)
    assert abs(min(uv.x for uv in face_uv)) < 1e-6
    assert abs(max(uv.x for uv in face_uv) - 1.0) < 1e-6
assert abs(min(uv.y for uv in marking_uv)) < 1e-6
assert abs(max(uv.y for uv in marking_uv) - 1.0) < 1e-6
assert len({round(uv.y, 4) for uv in marking_uv}) == 21
marked_polygon_count = len(basic_segment.data.polygons)

assert len(props.boundaries) == 7
assert sum(item.marking_enabled for item in props.boundaries) == 5
saved_marking_states = {item.boundary_id: item.marking_enabled for item in props.boundaries}
for boundary in props.boundaries:
    boundary.marking_enabled = False
road_builder.build_mode(bpy.context.scene, "basic")
unmarked_segment = bpy.data.objects["basic_segment"]
assert len(unmarked_segment.data.polygons) < marked_polygon_count
assert sum(polygon.material_index == 1 for polygon in unmarked_segment.data.polygons) == 0
for boundary in props.boundaries:
    boundary.marking_enabled = saved_marking_states[boundary.boundary_id]
road_builder.build_mode(bpy.context.scene, "basic")

props.node_shoulder_bands = True
road_builder.build_mode(bpy.context.scene, "basic")
shouldered_node_x = {round(vertex.co.x, 4) for vertex in bpy.data.objects["basic_node"].data.vertices}
assert -6.5 in shouldered_node_x and 6.5 in shouldered_node_x, shouldered_node_x
props.node_shoulder_bands = False
road_builder.build_mode(bpy.context.scene, "basic")

elevated_z = {round(vertex.co.z, 4) for vertex in bpy.data.objects["elevated_segment"].data.vertices}
assert 8.0 in elevated_z and 8.2 in elevated_z, elevated_z
elevated_segment = bpy.data.objects["elevated_segment"]
assert elevated_segment["cs1_girder_count"] == 6
assert elevated_segment["cs1_geometry_groups"] == "surface,deck,fascia,girder"
assert len(elevated_segment["cs1_girder_centers"]) == 6
assert elevated_segment["cs1_girder_spacing"] == 3.8
assert elevated_segment["cs1_girder_reference_span"] == 35.0
assert len(elevated_segment.data.materials) == 3
assert sum(polygon.material_index == 2 for polygon in elevated_segment.data.polygons) > 0
assert min(elevated_z) < 7.4
tunnel_z = {round(vertex.co.z, 4) for vertex in bpy.data.objects["tunnel_segment"].data.vertices}
assert -12.0 in tunnel_z and -11.8 in tunnel_z and -7.0 in tunnel_z, tunnel_z

left_edge_boundary = next(item for item in props.boundaries if item.boundary_id == "boundary-left-carriageway")
left_edge_boundary.marking_enabled = False
spec_output = ROOT / "build" / "smoke" / "roundtrip-road.json"
spec_output.parent.mkdir(parents=True, exist_ok=True)
assert bpy.ops.cs1_road.export_spec(filepath=str(spec_output)) == {"FINISHED"}
saved = json.loads(spec_output.read_text(encoding="utf-8"))
assert saved["schema_version"] == 3
assert saved["shared_geometry"]["segment_length"] == 64.0
assert saved["node"]["length"] == 64.0
assert saved["node"]["center_split"] is True
assert saved["node"]["shoulder_bands"] is False
assert round(saved["styles"]["markings"]["SOLID_WHITE"]["paint_width"], 3) == 0.15
assert round(saved["styles"]["markings"]["SOLID_WHITE"]["region_width"], 3) == 0.4
assert len(saved["layout"]["strips"]) == 8
assert len(saved["layout"]["boundaries"]) == 7
assert sum(item["marking"] is not None for item in saved["layout"]["boundaries"]) == 4
saved_boundaries = {item["id"]: item for item in saved["layout"]["boundaries"]}
assert saved_boundaries["boundary-left-carriageway"]["marking"] is None
assert saved_boundaries["boundary-right-carriageway"]["marking"]["role"] == "CARRIAGEWAY_EDGE"
assert len(saved["lanes"]) == 4
assert [lane["direction"] for lane in saved["lanes"]] == ["BACKWARD", "BACKWARD", "FORWARD", "FORWARD"]
assert len({lane["id"] for lane in saved["lanes"]}) == 4
assert all(lane["surface_strip_id"] for lane in saved["lanes"])

assert bpy.ops.cs1_road.import_spec(filepath=str(spec_output)) == {"FINISHED"}
roundtrip_boundaries = {item.boundary_id: item for item in props.boundaries}
assert roundtrip_boundaries["boundary-left-carriageway"].marking_enabled is False
assert roundtrip_boundaries["boundary-right-carriageway"].marking_enabled is True

example = ROOT / "specs" / "example-road.json"
assert bpy.ops.cs1_road.import_spec(filepath=str(example)) == {"FINISHED"}
assert len(props.lanes) == 4
assert props.lanes[0].zone == "LEFT_SIDEWALK"
assert props.lanes[0].lane_type == "PEDESTRIAN"
assert props.lanes[1].direction == "BACKWARD"
assert props.lanes[2].direction == "FORWARD"

panel = type("FakePanel", (), {"layout": FakeLayout()})()
road_builder.CS1ROAD_PT_main.draw(panel, bpy.context)

# Match Blender's F3 > Reload Scripts lifecycle. The package must refresh its
# child modules and register cleanly again without restarting Blender.
road_builder.unregister()
road_builder = importlib.reload(road_builder)
road_builder.register()
road_builder._initialize_scene_lanes()
reloaded_props = bpy.context.scene.cs1_road_builder
assert reloaded_props is not None
road_builder.build_mode(bpy.context.scene, "elevated")
_, reloaded_total_width, _ = road_builder._cross_section(reloaded_props)
reloaded_layout = road_builder.plan_main_girders(reloaded_total_width)
assert (
    bpy.data.objects["elevated_segment"]["cs1_girder_spacing"]
    == reloaded_layout.spacing
)

output = ROOT / "build" / "smoke" / "road-builder-addon-smoke.blend"
output.parent.mkdir(parents=True, exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=str(output))
print(f"ADDON_SMOKE_OK {output}")
