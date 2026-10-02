from __future__ import annotations

import importlib
import json
import shutil
import sys
from pathlib import Path

import bpy


ROOT = Path(__file__).resolve().parents[1]
ADDON_PARENT = ROOT / "blender_addon"
sys.path.insert(0, str(ADDON_PARENT))

road_builder = importlib.import_module("road_builder")

# Geometry and UV verification must not race Photoshop Generator while the PSD
# is being saved. Use deterministic atlas bases as test-only Generator output;
# production still requires textures/road-assets/*_d.png.
texture_fixture_root = ROOT / "build" / "smoke" / "texture-fixture"
texture_fixture_assets = texture_fixture_root / "road-assets"
texture_fixture_assets.mkdir(parents=True, exist_ok=True)
texture_manifest = json.loads(
    (ROOT / "textures" / "dimensions.json").read_text(encoding="utf-8")
)
for family in ("surface", "structure", "tunnel"):
    for map_id in ("d", "a", "p", "r", "n", "s"):
        shutil.copyfile(
            ROOT / "textures" / "atlas_bases" / f"{family}_base_2048.png",
            texture_fixture_assets / f"{family}_{map_id}.png",
        )
texture_fixture_manifest = texture_fixture_root / "dimensions.json"
texture_fixture_manifest.write_text(
    json.dumps(texture_manifest, ensure_ascii=False, indent=2) + "\n",
    encoding="utf-8",
)
road_builder.DEFAULT_TEXTURE_LAYOUT = texture_fixture_manifest
road_builder._TEXTURE_LAYOUT_CACHE = None
road_builder.register()


def create_elevated_edge_source(name, bottom_x, fence_x):
    mesh = bpy.data.meshes.new(f"{name}_mesh")
    mesh.from_pydata(
        (
            (0.0, -32.0, 0.0),
            (0.0, 32.0, 0.0),
            (bottom_x, 32.0, -1.0),
            (bottom_x, -32.0, -1.0),
            (fence_x, -32.0, 0.0),
            (fence_x, 32.0, 0.0),
            (fence_x, 32.0, 1.0),
            (fence_x, -32.0, 1.0),
        ),
        (),
        ((3, 2, 1, 0), (4, 5, 6, 7)),
    )
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    group = obj.vertex_groups.new(name="CS1_NO_SPLIT")
    group.add((4, 5, 6, 7), 1.0, "REPLACE")
    obj.location = (100.0, 50.0, 25.0)  # staging location is intentionally ignored
    return obj


def create_unsplit_edge_source(name, local_x=0.0000015):
    mesh = bpy.data.meshes.new(f"{name}_mesh")
    mesh.from_pydata(
        (
            (local_x, -32.0, 0.75),
            (local_x, 32.0, 0.75),
            (local_x, 32.0, -0.75),
            (local_x, -32.0, -0.75),
        ),
        (),
        ((0, 1, 2, 3),),
    )
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def create_box_edge_source(name, x_min=-0.4, x_max=0.4):
    mesh = bpy.data.meshes.new(f"{name}_mesh")
    mesh.from_pydata(
        (
            (x_min, -32.0, -0.75), (x_max, -32.0, -0.75),
            (x_max, 32.0, -0.75), (x_min, 32.0, -0.75),
            (x_min, -32.0, 0.75), (x_max, -32.0, 0.75),
            (x_max, 32.0, 0.75), (x_min, 32.0, 0.75),
        ),
        (),
        (
            (0, 3, 2, 1), (4, 5, 6, 7),
            (0, 1, 5, 4), (1, 2, 6, 5),
            (2, 3, 7, 6), (3, 0, 4, 7),
        ),
    )
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


class FakeLayout:
    """Minimal Blender UILayout stand-in that exercises the panel draw code."""

    layout_type = "DEFAULT"

    def __init__(self):
        self.operator_ids = []
        self.property_names = []

    def operator(self, operator_id, *args, **kwargs):
        self.operator_ids.append(operator_id)
        return self

    def prop(self, data, property_name, *args, **kwargs):
        self.property_names.append(property_name)
        return self

    def __getattr__(self, name):
        def method(*args, **kwargs):
            return self

        return method

road_builder._initialize_scene_lanes()
props = road_builder.active_road(bpy.context.scene)
assert len(bpy.context.scene.cs1_road_profiles) == 1, [
    (item.profile_id, item.name, road_builder._imt_appearance_data(item))
    for item in bpy.context.scene.cs1_road_profiles
]
assert props.profile_id == road_builder.DEFAULT_PROFILE_ID
profile = road_builder._profile_for_road(bpy.context.scene, props)
assert profile is not None
props.lanes.clear()
road_builder._add_default_lanes(props)
default_lane_offsets = {lane.lane_id: lane.vertical_offset for lane in props.lanes}
assert abs(default_lane_offsets["lane-left-sidewalk"] - road_builder.ROADWAY_DEPRESSION) < 1e-6
assert abs(default_lane_offsets["lane-right-sidewalk"] - road_builder.ROADWAY_DEPRESSION) < 1e-6
assert default_lane_offsets["lane-backward-1"] == 0.0
assert default_lane_offsets["lane-forward-1"] == 0.0
assert abs(props.lanes[0].width - 2.0) < 1e-6
assert abs(props.lanes[3].width - 2.0) < 1e-6
props.sidewalk_width = 3.0
assert abs(props.lanes[0].width - 2.5) < 1e-6
assert abs(props.lanes[3].width - 2.5) < 1e-6
props.sidewalk_width = 2.5
assert not road_builder._LIVE_PREVIEW_PENDING
props.depress_roadway = False
road_builder._sync_lane_derived_values(props)
assert all(lane.vertical_offset == 0.0 for lane in props.lanes)
props.depress_roadway = True
road_builder._sync_lane_derived_values(props)
left_sidewalk = props.lanes[0]
left_sidewalk.zone = "ROAD"
left_sidewalk.direction = "FORWARD"
left_sidewalk.lane_type = "VEHICLE"
left_sidewalk.vehicle_type = "CAR"
left_sidewalk.zone = "LEFT_SIDEWALK"
road_builder._sync_lane_derived_values(props)
assert left_sidewalk.direction == "BOTH"
assert left_sidewalk.lane_type == "PEDESTRIAN"
assert left_sidewalk.vehicle_type == "NONE"
centered_edge = create_unsplit_edge_source("CenteredUnsplitEdge")
centered_plan = road_builder._prepare_elevated_edge_mesh(
    centered_edge, "right", 10.5, 8.0, 1.0,
    road_builder.SEGMENT_SLICES, "CS1_NO_SPLIT",
)
assert len(centered_plan.polygons) == 20, len(centered_plan.polygons)
assert any(
    abs(point[0] - 10.5) < 1e-8 and abs(point[2] - 8.0) < 1e-8
    for polygon, _uvs in centered_plan.polygons for point in polygon
), "deck interface was not clipped at the road surface"
assert all(
    min(point[2] for point in polygon) >= 8.0 - 1e-8
    for polygon, _uvs in centered_plan.polygons
), "buried connector face remained below the road surface"
bpy.data.objects.remove(centered_edge, do_unlink=True)

offset_edge = create_unsplit_edge_source("OffsetUnsplitEdge", 0.375)
offset_plan = road_builder._prepare_elevated_edge_mesh(
    offset_edge, "right", 10.5, 8.0, 1.0,
    road_builder.SEGMENT_SLICES, "CS1_NO_SPLIT",
)
mirrored_offset_plan = road_builder._prepare_elevated_edge_mesh(
    offset_edge, "left", -10.5, 8.0, 1.0,
    road_builder.SEGMENT_SLICES, "CS1_NO_SPLIT",
)
assert abs(offset_plan.connector_x - 10.875) < 1e-8, offset_plan.connector_x
assert abs(mirrored_offset_plan.connector_x + 10.875) < 1e-8, mirrored_offset_plan.connector_x
assert any(
    abs(point[0] - 10.875) < 1e-8 and abs(point[2] - 8.0) < 1e-8
    for polygon, _uvs in offset_plan.polygons for point in polygon
), "local mesh offset from the placement origin was not preserved"
bpy.data.objects.remove(offset_edge, do_unlink=True)

box_edge = create_box_edge_source("BoxEdge")
right_box_plan = road_builder._prepare_elevated_edge_mesh(
    box_edge, "right", 10.5, 8.0, 1.0,
    road_builder.SEGMENT_SLICES, "CS1_NO_SPLIT",
)
left_box_plan = road_builder._prepare_elevated_edge_mesh(
    box_edge, "left", -10.5, 8.0, 1.0,
    road_builder.SEGMENT_SLICES, "CS1_NO_SPLIT",
)
assert abs(right_box_plan.connector_x - 10.1) < 1e-8, right_box_plan.connector_x
assert abs(left_box_plan.connector_x + 10.1) < 1e-8, left_box_plan.connector_x
assert len(right_box_plan.polygons) == 80, len(right_box_plan.polygons)
assert all(
    max(point[1] for point in polygon) - min(point[1] for point in polygon) > 1e-6
    for polygon, _uvs in right_box_plan.polygons
), "hidden 64 m end cap remained in the custom edge"
assert not any(
    all(abs(point[0] - 10.1) < 1e-8 for point in polygon)
    and min(point[2] for point in polygon) < 8.0 - 1e-8
    for polygon, _uvs in right_box_plan.polygons
), "buried road-centre-side face remained in the custom edge"
assert not any(
    abs(point[0] - 10.9) < 1e-8 and abs(point[2] - 8.0) < 1e-8
    for polygon, _uvs in right_box_plan.polygons for point in polygon
), "unnecessary surface-height loop was added to the exposed outer face"
bpy.data.objects.remove(box_edge, do_unlink=True)

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
props.between_sidewalks_width = 15.0
props.sidewalk_width = 3.0
props.depress_roadway = True
props.node_shoulder_bands = False
props.road_color = (0.20, 0.25, 0.30)
props.line_mesh_enabled = True
props.elevated_edge_mesh = create_elevated_edge_source(
    "ElevatedEdge", -0.6, 0.15
)

# Remaining width can become real symmetric Parking lanes.  If the requested
# width cannot fit both, no one-sided lane is left behind and the UI reports a
# warning instead of rejecting generation.
props.roadside_use = "PARKING"
props.parking_lane_width = 2.0
props.between_sidewalks_width = 17.0
parking_lanes = [lane for lane in props.lanes if lane.lane_type == "PARKING"]
assert {lane.lane_id for lane in parking_lanes} == road_builder.AUTO_PARKING_LANE_IDS
assert road_builder._cross_section_warning(props) is None
props.between_sidewalks_width = 16.9
assert not [lane for lane in props.lanes if lane.lane_type == "PARKING"]
assert "Parking needs" in road_builder._cross_section_warning(props)
props.roadside_use = "SHOULDER"
props.between_sidewalks_width = 15.0

props.between_sidewalks_width = 12.5
assert "exceeds the sidewalk span by 0.50 m" in road_builder._cross_section_warning(props)
assert bpy.ops.cs1_road.build_preview() == {"FINISHED"}
props.between_sidewalks_width = 15.0

result = bpy.ops.cs1_road.build_all()
assert result == {"FINISHED"}
center_boundaries = [
    item for item in props.boundaries
    if item.marking_enabled and item.marking_role == "CENTER_LINE"
]
assert len(center_boundaries) == 1
assert center_boundaries[0].marking_style == "DASHED_WHITE"

for mode in ("basic", "elevated", "bridge", "slope", "tunnel"):
    collection = bpy.data.collections.get(f"CS1_ROAD_{mode}")
    assert collection is not None, mode
    assert collection.hide_viewport is False, mode
    assert collection.hide_render is False, mode
    assert len(collection.objects) == 2, (mode, [obj.name for obj in collection.objects])
    assert collection.objects.get(f"{mode}_segment") is not None, mode
    assert collection.objects.get(f"{mode}_node") is not None, mode
    for obj in collection.objects:
        assert len(obj.data.uv_layers) == 1, obj.name
        for uv in (loop.uv for loop in obj.data.uv_layers[0].data):
            assert -1e-6 <= uv.x <= 1.0 + 1e-6, (obj.name, uv[:])
            assert -1e-6 <= uv.y <= 1.0 + 1e-6, (obj.name, uv[:])

shared_surface = bpy.data.materials[road_builder.SHARED_SURFACE_MATERIAL]
shared_structure = bpy.data.materials[road_builder.SHARED_STRUCTURE_MATERIAL]
shared_tunnel = bpy.data.materials[road_builder.SHARED_TUNNEL_MATERIAL]
assert tuple(round(value, 3) for value in shared_surface.diffuse_color) == (
    0.20, 0.25, 0.30, 1.0,
)
for material in (shared_surface, shared_structure, shared_tunnel):
    mapping_nodes = [node for node in material.node_tree.nodes if node.type == "MAPPING"]
    assert len(mapping_nodes) == 1, material.name
    assert tuple(mapping_nodes[0].inputs["Scale"].default_value) == (1.0, 2.0, 1.0)
    node_locations = [tuple(node.location) for node in material.node_tree.nodes]
    assert len(node_locations) == len(set(node_locations)), (
        material.name, node_locations,
    )
    image_nodes = [node for node in material.node_tree.nodes if node.type == "TEX_IMAGE"]
    assert {node.label for node in image_nodes} == {
        "CS1 Diffuse", "CS1 Alpha", "CS1 Pavement mask (runtime theme)",
        "CS1 Road mask (runtime theme)", "CS1 Normal", "CS1 Specular",
    }, material.name
surface_image_nodes = [
    node for node in shared_surface.node_tree.nodes if node.type == "TEX_IMAGE"
]
assert len(surface_image_nodes) == 6
assert all(tuple(node.image.size) == (2048, 2048) for node in surface_image_nodes)
assert {
    Path(node.image.filepath).name for node in surface_image_nodes
} == {f"surface_{map_id}.png" for map_id in ("d", "a", "p", "r", "n", "s")}
structure_image_nodes = [
    node for node in shared_structure.node_tree.nodes if node.type == "TEX_IMAGE"
]
tunnel_image_nodes = [
    node for node in shared_tunnel.node_tree.nodes if node.type == "TEX_IMAGE"
]
assert len(structure_image_nodes) == len(tunnel_image_nodes) == 6
assert {
    Path(node.image.filepath).name for node in structure_image_nodes
} == {f"structure_{map_id}.png" for map_id in ("d", "a", "p", "r", "n", "s")}
assert {
    Path(node.image.filepath).name for node in tunnel_image_nodes
} == {f"tunnel_{map_id}.png" for map_id in ("d", "a", "p", "r", "n", "s")}

surface_regions = {
    "lane.default", "shoulder.default", "sidewalk.default",
    "curb.upper", "curb.wall", "curb.lower",
    "sidewalk.default+curb.upper",
    "curb.lower+shoulder.default", "curb.lower+lane.default",
    "line.solid.white", "line.dashed.white",
}
structure_regions = {
    "deck.underside", "elevated.fascia", "bridge.fascia",
    "girder.bottom", "girder.side",
}
tunnel_regions = {"tunnel.roof", "tunnel.wall"}

locked_region_pixels = {
    "edge.sidewalk": ("surface", 224, 384),
    "curb.upper": ("surface", 384, 390),
    "curb.wall": ("surface", 390, 400),
    "curb.lower": ("surface", 400, 416),
    "edge.asphalt": ("surface", 416, 608),
    "shoulder.default": ("surface", 672, 704),
    "lane.default": ("surface", 768, 960),
    "sidewalk.default": ("surface", 1024, 1184),
    "line.solid.white": ("surface", 1987, 2013),
    "line.dashed.white": ("surface", 2019, 2045),
    "deck.underside": ("structure", 32, 800),
    "elevated.fascia": ("structure", 864, 928),
    "bridge.fascia": ("structure", 992, 1088),
    "girder.bottom": ("structure", 1152, 1196),
    "girder.side": ("structure", 1280, 1414),
    "tunnel.roof": ("tunnel", 32, 800),
    "tunnel.wall": ("tunnel", 864, 1184),
}
for region_id, (atlas_name, x_min, x_max) in locked_region_pixels.items():
    actual = road_builder._atlas_texture_region(atlas_name, region_id)[:2]
    assert actual == (x_min / 2048.0, x_max / 2048.0), (region_id, actual)

locked_profile_pixels = {
    "sidewalk.default+curb.upper": (224, 390),
    "curb.lower+shoulder.default": (400, 432),
    "curb.lower+lane.default": (400, 592),
}
for region_id, (x_min, x_max) in locked_profile_pixels.items():
    atlas_name, u_min, u_max = road_builder._uv_region_bounds(region_id)
    assert atlas_name == "surface", region_id
    assert (u_min, u_max) == (x_min / 2048.0, x_max / 2048.0), (
        region_id, u_min, u_max,
    )


def assert_exact_uv_regions(obj, allow_authored=False):
    region_attribute = obj.data.attributes["cs1_uv_region"]
    uv_layer = obj.data.uv_layers["RoadUV"]
    used = set()
    for polygon in obj.data.polygons:
        region_name = road_builder.UV_REGION_NAMES[
            region_attribute.data[polygon.index].value
        ]
        if region_name == "authored":
            assert allow_authored, (obj.name, polygon.index)
            continue
        atlas_name, u_min, u_max = road_builder._uv_region_bounds(region_name)
        if atlas_name == "surface":
            assert region_name in surface_regions
            assert polygon.material_index == 0, (obj.name, polygon.index, region_name)
        elif atlas_name == "structure":
            assert region_name in structure_regions
            assert polygon.material_index == 1, (obj.name, polygon.index, region_name)
        elif atlas_name == "tunnel":
            assert region_name in tunnel_regions
            assert polygon.material_index == 1, (obj.name, polygon.index, region_name)
        else:
            raise AssertionError((obj.name, polygon.index, region_name))
        if region_name.startswith("curb.lower+"):
            u_max = road_builder._texture_region("edge.asphalt")[1]
        face_u = [uv_layer.data[index].uv.x for index in polygon.loop_indices]
        assert min(face_u) >= u_min - 1e-6, (
            obj.name, polygon.index, region_name, min(face_u), u_min,
        )
        assert max(face_u) <= u_max + 1e-6, (
            obj.name, polygon.index, region_name, max(face_u), u_max,
        )
        used.add(region_name)
    return used


initial_regions = {}
for mode in ("basic", "elevated", "bridge", "slope", "tunnel"):
    segment = bpy.data.objects[f"{mode}_segment"]
    node = bpy.data.objects[f"{mode}_node"]
    assert segment.data.materials[0] == shared_surface, mode
    assert node.data.materials[0] == shared_surface, mode
    if mode in {"elevated", "bridge"}:
        assert segment.data.materials[1] == shared_structure, mode
        assert node.data.materials[1] == shared_structure, mode
    elif mode in {"slope", "tunnel"}:
        assert segment.data.materials[1] == shared_tunnel, mode
        assert node.data.materials[1] == shared_tunnel, mode
    else:
        assert len(segment.data.materials) == 1, mode
        assert len(node.data.materials) == 1, mode
    initial_regions[(mode, "segment")] = assert_exact_uv_regions(segment)
    initial_regions[(mode, "node")] = assert_exact_uv_regions(node)

assert initial_regions[("basic", "segment")] == {
    "lane.default", "curb.lower+shoulder.default",
    "sidewalk.default+curb.upper",
    "curb.wall", "line.solid.white", "line.dashed.white",
}
assert "line.solid.white" not in initial_regions[("basic", "node")]
road_half = road_builder._cross_section(props)[0] * 0.5
outer_uv_spans = [
    span for span in road_builder._road_uv_spans(props, road_half)
    if span.uv_region_id == "curb.lower+shoulder.default"
]
assert len(outer_uv_spans) == 2
for span in outer_uv_spans:
    assert abs((span.u_range[1] - span.u_range[0]) * 2048.0 - 64.0) < 1e-6
    curb_visible_width = (span.x_max - span.x_min) * 16.0 / 64.0
    assert abs(curb_visible_width - 0.25) < 1e-6
assert {"deck.underside", "elevated.fascia", "girder.bottom", "girder.side"} <= initial_regions[("elevated", "segment")]
assert {"deck.underside", "bridge.fascia", "girder.bottom", "girder.side"} <= initial_regions[("bridge", "segment")]
assert tunnel_regions <= initial_regions[("tunnel", "segment")]
assert tunnel_regions <= initial_regions[("slope", "segment")]

structure_ranges = [
    road_builder._atlas_texture_region("structure", region_id)[:2]
    for region_id in (
        "deck.underside", "bridge.fascia",
        "girder.bottom", "girder.side",
    )
]
bridge_mesh = bpy.data.objects["bridge_segment"].data
bridge_uv = bridge_mesh.uv_layers[0]
for polygon in bridge_mesh.polygons:
    if polygon.material_index != 1:
        continue
    face_u = [bridge_uv.data[index].uv.x for index in polygon.loop_indices]
    assert any(
        min(face_u) >= u_min - 1e-6 and max(face_u) <= u_max + 1e-6
        for u_min, u_max in structure_ranges
    ), (polygon.index, min(face_u), max(face_u))

tunnel_ranges = [
    road_builder._atlas_texture_region("tunnel", region_id)[:2]
    for region_id in ("tunnel.roof", "tunnel.wall")
]
tunnel_mesh = bpy.data.objects["tunnel_segment"].data
tunnel_uv = tunnel_mesh.uv_layers[0]
for polygon in tunnel_mesh.polygons:
    if polygon.material_index != 1:
        continue
    face_u = [tunnel_uv.data[index].uv.x for index in polygon.loop_indices]
    assert any(
        min(face_u) >= u_min - 1e-6 and max(face_u) <= u_max + 1e-6
        for u_min, u_max in tunnel_ranges
    ), (polygon.index, min(face_u), max(face_u))

for mode_index, mode in enumerate(("basic", "elevated", "bridge", "slope", "tunnel")):
    for part in ("segment", "node"):
        obj = bpy.data.objects[f"{mode}_{part}"]
        assert obj.location.x == 0.0, (obj.name, obj.location[:])
        assert obj.location.z == mode_index * 16.0, (obj.name, obj.location[:])
        roadway_faces = [
            polygon for polygon in obj.data.polygons
            if abs(polygon.center.x) < 6.5 - 1e-5
            and polygon.normal.z > 0.9
        ]
        assert roadway_faces, obj.name
        roadway_z = {
            round(obj.data.vertices[index].co.z, 6)
            for polygon in roadway_faces for index in polygon.vertices
        }
        assert roadway_z == {-0.3}, (obj.name, roadway_z)

left_node = bpy.data.objects["basic_node"]
node_top_z = sorted({round(vertex.co.z, 4) for vertex in left_node.data.vertices})
assert -0.3 in node_top_z and 0.0 in node_top_z, node_top_z
assert left_node["cs1_center_split"] is True
assert left_node.location.y == 32.0

basic_segment = bpy.data.objects["basic_segment"]
assert len(basic_segment.data.uv_layers) == 1
def polygons_of_kind(obj, kind):
    attribute = obj.data.attributes["cs1_face_kind"]
    value = road_builder.FACE_KIND_VALUES[kind]
    return [
        polygon for polygon in obj.data.polygons
        if attribute.data[polygon.index].value == value
    ]


assert len(polygons_of_kind(basic_segment, "marking")) == 100
assert len(polygons_of_kind(left_node, "marking")) == 0
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
assert node_x == {
    -10.5, -7.5, -6.5, -3.25,
    0.0,
    3.25, 6.5, 7.5, 10.5,
}, node_x
assert not ({-7.6, -7.25, 7.25, 7.6} & node_x), node_x


wall_u_min, wall_u_max, _ = road_builder._texture_region("curb.wall")
curb_wall_faces = [
    polygon for polygon in basic_segment.data.polygons
    if abs(abs(polygon.center.x) - 7.5) < 1e-6
    and abs(polygon.normal.x) > 0.99
]
assert len(curb_wall_faces) == 40
wall_uv_layer = basic_segment.data.uv_layers["RoadUV"]
for polygon in curb_wall_faces:
    face_u = [
        wall_uv_layer.data[index].uv.x for index in polygon.loop_indices
    ]
    assert abs(min(face_u) - wall_u_min) < 1e-6
    assert abs(max(face_u) - wall_u_max) < 1e-6


def u_values_at_coordinate(obj, coordinate):
    vertex_indices = {
        vertex.index for vertex in obj.data.vertices
        if all(abs(vertex.co[index] - coordinate[index]) < 1e-6 for index in range(3))
    }
    layer = obj.data.uv_layers["RoadUV"]
    return {
        round(layer.data[loop.index].uv.x, 6)
        for loop in obj.data.loops if loop.vertex_index in vertex_indices
    }


assert u_values_at_coordinate(basic_segment, (-7.5, -32.0, 0.0)) == {
    round(wall_u_min, 6)
}
assert u_values_at_coordinate(basic_segment, (7.5, -32.0, 0.0)) == {
    round(wall_u_min, 6)
}
assert u_values_at_coordinate(basic_segment, (-7.5, -32.0, -0.3)) == {
    round(wall_u_max, 6)
}
assert u_values_at_coordinate(basic_segment, (7.5, -32.0, -0.3)) == {
    round(wall_u_max, 6)
}

uv_layer = basic_segment.data.uv_layers[0]
for uv in (loop.uv for loop in uv_layer.data):
    assert -1e-6 <= uv.x <= 1.0 + 1e-6, uv[:]
    assert -1e-6 <= uv.y <= 1.0 + 1e-6, uv[:]
marking_uv = []
marking_region_attribute = basic_segment.data.attributes["cs1_uv_region"]
for polygon in polygons_of_kind(basic_segment, "marking"):
    region_name = road_builder.UV_REGION_NAMES[
        marking_region_attribute.data[polygon.index].value
    ]
    line_u_min, line_u_max, _ = road_builder._texture_region(region_name)
    face_uv = [uv_layer.data[index].uv for index in polygon.loop_indices]
    marking_uv.extend(face_uv)
    assert abs(min(uv.x for uv in face_uv) - line_u_min) < 1e-6
    assert abs(max(uv.x for uv in face_uv) - line_u_max) < 1e-6
    assert abs((max(uv.x for uv in face_uv) - min(uv.x for uv in face_uv)) * 2048.0 - 26.0) < 1e-5
solid_u_min, solid_u_max, _ = road_builder._texture_region("line.solid.white")
assert abs((solid_u_max - solid_u_min) * 2048.0 - 26.0) < 1e-6
assert abs((solid_u_min + solid_u_max) * 0.5 * 2048.0 - 2000.0) < 1e-6
assert abs(min(uv.y for uv in marking_uv)) < 1e-6
assert abs(max(uv.y for uv in marking_uv) - 1.0) < 1e-6
assert len({round(uv.y, 4) for uv in marking_uv}) == 21

surface_region_ids = (
    "lane.default", "curb.lower+shoulder.default",
    "sidewalk.default+curb.upper", "curb.wall",
)
surface_u_ranges = [road_builder._uv_region_bounds(item)[1:] for item in surface_region_ids]
surface_u_ranges.extend(
    span.u_range
    for span in road_builder._road_uv_spans(
        props, road_builder._cross_section(props)[0] * 0.5,
    )
    if span.u_range is not None
)
for polygon in polygons_of_kind(basic_segment, "surface"):
    face_u = [uv_layer.data[index].uv.x for index in polygon.loop_indices]
    assert any(
        min(face_u) >= u_min - 1e-6 and max(face_u) <= u_max + 1e-6
        for u_min, u_max in surface_u_ranges
    ), (polygon.index, min(face_u), max(face_u))
node_uv_x = {
    round(loop.uv.x, 6) for loop in left_node.data.uv_layers[0].data
}
for region_id in surface_region_ids:
    _atlas_name, u_min, u_max = road_builder._uv_region_bounds(region_id)
    dynamic_ranges = [
        span.u_range
        for span in road_builder._road_uv_spans(
            props, road_builder._cross_section(props)[0] * 0.5,
        )
        if span.uv_region_id == region_id and span.u_range is not None
    ]
    if dynamic_ranges:
        u_min = min(item[0] for item in dynamic_ranges)
        u_max = max(item[1] for item in dynamic_ranges)
    assert round(u_min, 6) in node_uv_x, (region_id, u_min, sorted(node_uv_x))
    assert round(u_max, 6) in node_uv_x, (region_id, u_max, sorted(node_uv_x))

props.lane_separator_style = "SOLID_WHITE"
props.center_line_style = "DASHED_WHITE"
road_builder.build_mode(bpy.context.scene, "basic")
styled_segment = bpy.data.objects["basic_segment"]
styled_regions = assert_exact_uv_regions(styled_segment)
assert {"line.solid.white", "line.dashed.white"} <= styled_regions
styled_uv = styled_segment.data.uv_layers[0]
marking_ranges = {
    tuple(round(styled_uv.data[index].uv.x, 6) for index in polygon.loop_indices)
    for polygon in polygons_of_kind(styled_segment, "marking")
}
solid_u = tuple(round(value, 6) for value in road_builder._texture_region("line.solid.white")[:2])
dashed_u = tuple(round(value, 6) for value in road_builder._texture_region("line.dashed.white")[:2])
assert abs((road_builder._texture_region("line.dashed.white")[1] - road_builder._texture_region("line.dashed.white")[0]) * 2048.0 - 26.0) < 1e-6
assert abs(sum(road_builder._texture_region("line.dashed.white")[:2]) * 0.5 * 2048.0 - 2032.0) < 1e-6
assert any(min(values) == solid_u[0] and max(values) == solid_u[1] for values in marking_ranges)
assert any(min(values) == dashed_u[0] and max(values) == dashed_u[1] for values in marking_ranges)
props.lane_separator_style = "DASHED_WHITE"
road_builder.build_mode(bpy.context.scene, "basic")

# A generated median replaces the opposing-lane marking and the road surface
# beneath it.  Its two curb tops and unsplit centre top remain distinct faces.
props.median_profile = "CURB"
props.median_width = 1.2
props.median_height = 0.2
props.median_curb_width = 0.15
assert bpy.ops.cs1_road.build_all() == {"FINISHED"}
for mode in ("basic", "elevated", "bridge", "slope", "tunnel"):
    for part in ("segment", "node"):
        median_object = bpy.data.objects[f"{mode}_{part}"]
        assert abs(median_object["cs1_median_width"] - 1.2) < 1e-5
        assert abs(median_object["cs1_median_height"] - 0.2) < 1e-5
median_segment = bpy.data.objects["basic_segment"]
assert abs(median_segment["cs1_median_width"] - 1.2) < 1e-5
assert abs(median_segment["cs1_median_height"] - 0.2) < 1e-5
assert median_segment["cs1_median_with_curb"] is True
assert len(props.boundaries) == 8
assert {item.boundary_id for item in props.boundaries if item.role == "MEDIAN_EDGE"} == {
    "boundary-median-left", "boundary-median-right",
}
assert len(polygons_of_kind(median_segment, "marking")) == 80
median_top_faces = [
    polygon for polygon in median_segment.data.polygons
    if polygon.normal.z > 0.99 and abs(polygon.center.z + 0.1) < 1e-5
]
assert len(median_top_faces) == 60, len(median_top_faces)
median_centre_faces = [
    polygon for polygon in median_top_faces if abs(polygon.center.x) < 1e-6
]
assert len(median_centre_faces) == 20
for polygon in median_centre_faces:
    xs = {round(median_segment.data.vertices[index].co.x, 4) for index in polygon.vertices}
    assert xs == {-0.45, 0.45}, xs
assert not any(
    polygon.normal.z > 0.99
    and abs(polygon.center.z + 0.3) < 1e-5
    and abs(polygon.center.x) < 0.59
    for polygon in median_segment.data.polygons
), "road surface remained beneath the median"
median_y = [
    vertex.co.y for vertex in median_segment.data.vertices
    if abs(abs(vertex.co.x) - 0.6) < 1e-5
]
assert round(min(median_y), 3) == -32.002
assert round(max(median_y), 3) == 32.002
assert any(
    abs(vertex.co.x + 0.6) < 1e-5 and abs(vertex.co.z + 0.302) < 1e-5
    for vertex in median_segment.data.vertices
), "median corner was not lowered below the roadway"
median_walls = [
    polygon for polygon in median_segment.data.polygons
    if abs(abs(polygon.center.x) - 0.6) < 1e-5 and abs(polygon.normal.x) > 0.99
]
assert any(polygon.center.x < 0.0 and polygon.normal.x < 0.0 for polygon in median_walls)
assert any(polygon.center.x > 0.0 and polygon.normal.x > 0.0 for polygon in median_walls)
median_spec_path = ROOT / "build" / "smoke" / "median-road.json"
assert bpy.ops.cs1_road.export_spec(filepath=str(median_spec_path)) == {"FINISHED"}
median_spec = json.loads(median_spec_path.read_text(encoding="utf-8"))
assert median_spec["shared_geometry"]["median"] == {
    "enabled": True,
    "profile": "CURB",
    "width": props.median_width,
    "height": props.median_height,
    "with_curb": True,
    "curb_top_width": props.median_curb_width,
    "mesh_object": None,
    "no_split_group": "CS1_NO_SPLIT",
}
assert sum(
    strip["function"] == "MEDIAN" for strip in median_spec["layout"]["strips"]
) == 1
assert sum(
    boundary["role"] == "MEDIAN_EDGE"
    for boundary in median_spec["layout"]["boundaries"]
) == 2
props.median_profile = "NONE"
assert bpy.ops.cs1_road.import_spec(filepath=str(median_spec_path)) == {"FINISHED"}
assert props.median_enabled is True
assert props.median_profile == "CURB"
assert abs(props.median_width - 1.2) < 1e-5
assert abs(props.median_height - 0.2) < 1e-5
invalid_median_spec = json.loads(json.dumps(median_spec))
next(
    strip for strip in invalid_median_spec["layout"]["strips"]
    if strip["id"] == "strip-median"
)["width"] = 1.3
invalid_median_path = ROOT / "build" / "smoke" / "median-road-invalid.json"
invalid_median_path.write_text(
    json.dumps(invalid_median_spec, indent=2), encoding="utf-8"
)
try:
    invalid_result = bpy.ops.cs1_road.import_spec(filepath=str(invalid_median_path))
except RuntimeError as error:
    assert "median width and strip-median width disagree" in str(error)
else:
    assert invalid_result == {"CANCELLED"}

# Without generated curbs, one supplied mesh is fitted to the configured
# width/height, sliced longitudinally, and has its hidden end caps removed.
median_source = create_box_edge_source("MedianSource", -1.0, 1.0)
props.median_profile = "MESH"
props.median_mesh = median_source
road_builder.build_mode(bpy.context.scene, "basic")
custom_median = bpy.data.objects["basic_segment"]
assert custom_median["cs1_median_source"] == "MedianSource"
custom_median_regions = assert_exact_uv_regions(custom_median)
assert "sidewalk.default" in custom_median_regions
custom_top_faces = [
    polygon for polygon in custom_median.data.polygons
    if polygon.normal.z > 0.99
    and abs(polygon.center.z + 0.1) < 1e-5
    and abs(polygon.center.x) < 1e-6
]
assert len(custom_top_faces) == 20, len(custom_top_faces)
for polygon in custom_top_faces:
    xs = {round(custom_median.data.vertices[index].co.x, 4) for index in polygon.vertices}
    assert xs == {-0.6, 0.6}, xs
assert all(
    max(custom_median.data.vertices[index].co.y for index in polygon.vertices)
    - min(custom_median.data.vertices[index].co.y for index in polygon.vertices)
    > 1e-6
    for polygon in custom_median.data.polygons
), "custom median end cap remained"
props.median_profile = "NONE"
props.median_mesh = None
bpy.data.objects.remove(median_source, do_unlink=True)
assert bpy.ops.cs1_road.build_all() == {"FINISHED"}
basic_segment = bpy.data.objects["basic_segment"]
marked_polygon_count = len(basic_segment.data.polygons)

# The global switch removes only generated line bands. Boundary definitions
# remain enabled so IMT/export can keep using their semantic roles.
props.line_mesh_enabled = False
road_builder.build_mode(bpy.context.scene, "basic")
meshless_lines_segment = bpy.data.objects["basic_segment"]
assert len(polygons_of_kind(meshless_lines_segment, "marking")) == 0
assert sum(item.marking_enabled for item in props.boundaries) == 5, [
    (item.boundary_id, item.marking_enabled) for item in props.boundaries
]
assert len(meshless_lines_segment.data.polygons) < marked_polygon_count
props.line_mesh_enabled = True
road_builder.build_mode(bpy.context.scene, "basic")

assert len(props.boundaries) == 7
assert sum(item.marking_enabled for item in props.boundaries) == 5
props.roadside_lines = False
road_builder.build_mode(bpy.context.scene, "basic")
no_roadside_segment = bpy.data.objects["basic_segment"]
assert len(no_roadside_segment.data.polygons) < marked_polygon_count
assert sum(item.marking_enabled for item in props.boundaries) == 3
props.roadside_lines = True
road_builder.build_mode(bpy.context.scene, "basic")

props.node_shoulder_bands = True
road_builder.build_mode(bpy.context.scene, "basic")
shouldered_node_x = {round(vertex.co.x, 4) for vertex in bpy.data.objects["basic_node"].data.vertices}
assert -6.5 in shouldered_node_x and 6.5 in shouldered_node_x, shouldered_node_x
props.node_shoulder_bands = False
road_builder.build_mode(bpy.context.scene, "basic")

# A recessed road uses its regular level node.  Only a flush road owns the
# preview node which descends to the recessed node surface.
props.depress_roadway = False
road_builder.build_mode(bpy.context.scene, "basic")
flush_segment_z = {round(vertex.co.z, 4) for vertex in bpy.data.objects["basic_segment"].data.vertices}
flush_transition_node_z = {round(vertex.co.z, 4) for vertex in bpy.data.objects["basic_node"].data.vertices}
assert flush_segment_z == {0.0}, flush_segment_z
assert -0.3 in flush_transition_node_z and 0.0 in flush_transition_node_z, flush_transition_node_z
props.depress_roadway = True
road_builder.build_mode(bpy.context.scene, "basic")
depressed_segment_z = {round(vertex.co.z, 4) for vertex in bpy.data.objects["basic_segment"].data.vertices}
depressed_node_z = {round(vertex.co.z, 4) for vertex in bpy.data.objects["basic_node"].data.vertices}
assert depressed_segment_z == {-0.3, 0.0}, depressed_segment_z
assert depressed_node_z == {-0.3, 0.0}, depressed_node_z

elevated_z = {round(vertex.co.z, 4) for vertex in bpy.data.objects["elevated_segment"].data.vertices}
assert -0.3 in elevated_z and 0.0 in elevated_z, elevated_z
elevated_segment = bpy.data.objects["elevated_segment"]
assert elevated_segment["cs1_girder_count"] == 6
assert elevated_segment["cs1_geometry_groups"] == "surface,deck,custom_edge,girder"
assert len(elevated_segment["cs1_girder_centers"]) == 6
assert elevated_segment["cs1_girder_spacing"] == 3.8
assert elevated_segment["cs1_girder_reference_span"] == 35.0
assert len(elevated_segment.data.materials) == 2
assert sum(polygon.material_index == 1 for polygon in elevated_segment.data.polygons) > 0
assert min(elevated_z) < -0.6
assert elevated_segment["cs1_elevated_edge_source"] == "ElevatedEdge"
assert elevated_segment["cs1_elevated_left_edge_mirrored"] is True


def y_values_at_xz(obj, x, z):
    return {
        round(vertex.co.y, 4)
        for vertex in obj.data.vertices
        if abs(vertex.co.x - x) < 1e-5 and abs(vertex.co.z - z) < 1e-5
    }


# The structural side face is sliced; the disconnected fence face is not.
assert len(y_values_at_xz(elevated_segment, -9.9, -1.0)) == 21
assert len(y_values_at_xz(elevated_segment, 9.9, -1.0)) == 21
assert y_values_at_xz(elevated_segment, -10.65, 1.0) == {-32.0, 32.0}
assert y_values_at_xz(elevated_segment, 10.65, 1.0) == {-32.0, 32.0}
elevated_node = bpy.data.objects["elevated_node"]
assert len(y_values_at_xz(elevated_node, -9.9, -1.0)) == 9

# Mirroring reverses face order so outward normals remain opposite.
edge_side_faces = [
    polygon for polygon in elevated_segment.data.polygons
    if abs(polygon.center.x) > 9.8
    and -1.0 - 1e-4 <= polygon.center.z <= 0.0 + 1e-4
    and abs(polygon.normal.x) > 0.1
]
edge_normal_samples = [
    (tuple(polygon.center), tuple(polygon.normal)) for polygon in edge_side_faces
]
assert any(
    polygon.center.x < 0.0 and polygon.normal.x < 0.0
    for polygon in edge_side_faces
), edge_normal_samples
assert any(
    polygon.center.x > 0.0 and polygon.normal.x > 0.0
    for polygon in edge_side_faces
), edge_normal_samples

# Custom lowest edge replaces the fixed vertical fascia and shares the deck
# underside vertex, producing one connected mesh.
assert not y_values_at_xz(elevated_segment, -10.5, -1.0)
join_vertices = [
    vertex.index for vertex in elevated_segment.data.vertices
    if abs(vertex.co.x + 9.9) < 1e-5
    and abs(vertex.co.y + 32.0) < 1e-5
    and abs(vertex.co.z + 1.0) < 1e-5
]
assert len(join_vertices) == 1, [
    (
        index,
        tuple(elevated_segment.data.vertices[index].co),
        [polygon.index for polygon in elevated_segment.data.polygons if index in polygon.vertices],
    )
    for index in join_vertices
]
assert sum(join_vertices[0] in polygon.vertices for polygon in elevated_segment.data.polygons) >= 2

# Only the below-origin portion follows the configured deck depth.
props.deck_depth = 2.0
road_builder.build_mode(bpy.context.scene, "elevated")
depth_scaled = bpy.data.objects["elevated_segment"]
assert len(y_values_at_xz(depth_scaled, -9.9, -2.0)) == 21
assert y_values_at_xz(depth_scaled, -10.65, 1.0) == {-32.0, 32.0}
props.deck_depth = 1.0
road_builder.build_mode(bpy.context.scene, "elevated")
tunnel_z = {round(vertex.co.z, 4) for vertex in bpy.data.objects["tunnel_segment"].data.vertices}
assert -0.3 in tunnel_z and 0.0 in tunnel_z and 4.7 in tunnel_z, tunnel_z

props.roadside_lines = False
props.lane_separator_style = "SOLID_WHITE"
props.center_line_style = "SOLID_YELLOW"
props.line_mesh_enabled = False
props.node_min_corner_offset = 12.0
spec_output = ROOT / "build" / "smoke" / "roundtrip-road.json"
spec_output.parent.mkdir(parents=True, exist_ok=True)
assert bpy.ops.cs1_road.export_spec(filepath=str(spec_output)) == {"FINISHED"}
saved = json.loads(spec_output.read_text(encoding="utf-8"))
assert saved["schema_version"] == 4
assert saved["profile_id"] == props.profile_id
assert saved["shared_geometry"]["segment_length"] == 64.0
assert saved["shared_geometry"]["line_mesh_enabled"] is False
assert saved["node"]["length"] == 64.0
assert saved["node"]["center_split"] is True
assert saved["node"]["shoulder_bands"] is False
assert saved["node"]["min_corner_offset"] == 12.0
assert round(saved["styles"]["markings"]["SOLID_WHITE"]["paint_width"], 3) == 0.15
assert round(saved["styles"]["markings"]["SOLID_WHITE"]["region_width"], 3) == 0.4
assert saved["styles"]["markings"]["rules"] == {
    "roadside_lines": False,
    "lane_separator_style": "SOLID_WHITE",
    "center_line_style": "SOLID_YELLOW",
}
assert [round(value, 3) for value in saved["styles"]["surface"]["road_color"]] == [
    0.20, 0.25, 0.30,
]
assert "imt_preview" not in saved["styles"]
assert len(saved["layout"]["strips"]) == 8
assert len(saved["layout"]["boundaries"]) == 7
assert sum(item["marking"] is not None for item in saved["layout"]["boundaries"]) == 3
saved_boundaries = {item["id"]: item for item in saved["layout"]["boundaries"]}
assert saved_boundaries["boundary-left-carriageway"]["marking"] is None
assert saved_boundaries["boundary-right-carriageway"]["marking"] is None
assert saved_boundaries["boundary-lane-2-lane-3"]["marking"]["style_id"] == "SOLID_YELLOW"
assert len(saved["lanes"]) == 4
assert [lane["direction"] for lane in saved["lanes"]] == ["BACKWARD", "BACKWARD", "FORWARD", "FORWARD"]
assert len({lane["id"] for lane in saved["lanes"]}) == 4
assert all(lane["surface_strip_id"] for lane in saved["lanes"])
assert all(
    not {"speed_limit", "stop_offset", "allow_connect"}.intersection(lane)
    for lane in saved["lanes"]
)

props.road_color = (0.8, 0.8, 0.8)
props.line_mesh_enabled = True
assert bpy.ops.cs1_road.import_spec(filepath=str(spec_output)) == {"FINISHED"}
roundtrip_boundaries = {item.boundary_id: item for item in props.boundaries}
assert roundtrip_boundaries["boundary-left-carriageway"].marking_enabled is False
assert roundtrip_boundaries["boundary-right-carriageway"].marking_enabled is False
assert props.roadside_lines is False
assert props.lane_separator_style == "SOLID_WHITE"
assert props.center_line_style == "SOLID_YELLOW"
assert props.node_min_corner_offset == 12.0
assert props.line_mesh_enabled is False
assert [round(value, 3) for value in props.road_color] == [0.20, 0.25, 0.30]

example = ROOT / "specs" / "example-road.json"
assert bpy.ops.cs1_road.import_spec(filepath=str(example)) == {"FINISHED"}
assert len(props.lanes) == 4
assert props.lanes[0].zone == "LEFT_SIDEWALK"
assert props.lanes[0].lane_type == "PEDESTRIAN"
assert props.lanes[1].direction == "BACKWARD"
assert props.lanes[2].direction == "FORWARD"
assert props.node_min_corner_offset == 0.0
assert [round(value, 3) for value in props.road_color] == [0.12, 0.14, 0.16]
profile = road_builder._profile_for_road(bpy.context.scene, props)
assert profile.imt_appearance_preset == "JP_WEATHERED"
assert props.roadside_lines is True
assert props.lane_separator_style == "DASHED_WHITE"
assert props.center_line_style == "DASHED_WHITE"
profile.imt_texture = 0.30
assert profile.imt_appearance_preset == "CUSTOM"
profile.imt_appearance_preset = "JP_WEATHERED"
assert round(profile.imt_texture, 2) == 0.25
profile_count = len(bpy.context.scene.cs1_road_profiles)
source_profile_id = profile.profile_id
source_profile_preset = profile.imt_appearance_preset
assert bpy.ops.cs1_road.profile_duplicate() == {"FINISHED"}
duplicated_profile = road_builder._active_profile(bpy.context.scene)
assert duplicated_profile.imt_appearance_preset == source_profile_preset
assert props.profile_id == duplicated_profile.profile_id
props.profile_id = source_profile_id
assert bpy.ops.cs1_road.profile_remove() == {"FINISHED"}
assert len(bpy.context.scene.cs1_road_profiles) == profile_count
bpy.context.scene.cs1_active_profile_index = 0
profile = road_builder._profile_for_road(bpy.context.scene, props)
vehicle_lane = next(lane for lane in props.lanes if lane.zone == "ROAD")
vehicle_lane_index = next(
    index for index, lane in enumerate(props.lanes) if lane.as_pointer() == vehicle_lane.as_pointer()
)
profile.override_road_speed_limit = False
profile.road_speed_limit = 9.0
assert bpy.ops.cs1_road.profile_override_toggle(field="road_speed_limit") == {"FINISHED"}
assert profile.override_road_speed_limit and profile.road_speed_limit == 1.0
assert bpy.ops.cs1_road.profile_override_toggle(field="road_speed_limit") == {"FINISHED"}
assert not profile.override_road_speed_limit
profile.override_road_speed_limit = True
profile.road_speed_limit = 1.5
vehicle_lane.override_speed_limit = False
resolved = road_builder._lane_setting(profile, vehicle_lane, "speed_limit")
assert resolved.value == 1.5 and resolved.source == profile.profile_id
assert bpy.ops.cs1_road.lane_override_toggle(
    index=vehicle_lane_index, field="speed_limit",
) == {"FINISHED"}
assert vehicle_lane.override_speed_limit and vehicle_lane.speed_limit == 1.5
inherited = road_builder._inherited_lane_setting(profile, vehicle_lane, "speed_limit")
assert road_builder.redundant_override(vehicle_lane.speed_limit, inherited.value)
assert bpy.ops.cs1_road.lane_override_toggle(
    index=vehicle_lane_index, field="speed_limit",
) == {"FINISHED"}
assert not vehicle_lane.override_speed_limit
profile.override_road_speed_limit = False
props.road_color = (0.31, 0.32, 0.33)

runtime_output = ROOT / "build" / "smoke" / "runtime-preview"
props.runtime_output_dir = str(runtime_output)
props.runtime_road_id = "smoke-road"
props.runtime_prefab_name = "Smoke Road"
props.roadside_lines = False
props.lane_separator_style = "SOLID_WHITE"
props.center_line_style = "SOLID_YELLOW"
assert bpy.ops.cs1_road.export_runtime() == {"FINISHED"}
manifest = json.loads((runtime_output / "manifest.json").read_text(encoding="utf-8"))
bundle = json.loads((runtime_output / "roads" / "smoke-road.json").read_text(encoding="utf-8"))
assert manifest["schema_version"] == 2
assert bundle["schema_version"] == 2
assert manifest["roads"][0]["revision"] == bundle["revision"]
assert len(manifest["texture_revision"]) == 64
for family in ("surface", "structure", "tunnel"):
    for map_id in ("d", "a", "p", "r", "n", "s"):
        filename = f"{family}_{map_id}.png"
        source = texture_fixture_assets / filename
        staged = runtime_output / "textures" / filename
        assert staged.read_bytes() == source.read_bytes(), filename
assert bundle["half_width"] == 6.0
assert bundle["pavement_width"] == 2.5
assert bundle["node_min_corner_offset"] == 0.0
assert [round(value, 3) for value in bundle["imt_marking_style"]["cracks"]] == [0.7, 0.4]
assert [round(value, 3) for value in bundle["imt_marking_style"]["voids"]] == [0.2, 1.0]
assert round(bundle["imt_marking_style"]["line_width"], 3) == 0.15
assert bundle["imt_marking_style"]["crosswalk_width"] == 3.0
assert bundle["imt_marking_style"]["center_line_yellow"] is True
assert bundle["imt_marking_style"]["roadside_lines"] is False
assert bundle["imt_marking_style"]["lane_separator_style"] == "SOLID_WHITE"
assert bundle["imt_marking_style"]["center_line_style"] == "SOLID_YELLOW"
assert len(bundle["revision"]) == 64
assert len(bundle["structural_signature"]) == 64
mode_entries = {item["mode"]: item["entries"] for item in bundle["modes"]}
assert set(mode_entries) == {"basic", "elevated", "bridge", "slope", "tunnel"}
expected_materials = {
    "basic": [road_builder.SHARED_SURFACE_MATERIAL] * 2,
    "elevated": [
        road_builder.SHARED_SURFACE_MATERIAL,
        road_builder.SHARED_STRUCTURE_MATERIAL,
    ] * 2,
    "bridge": [
        road_builder.SHARED_SURFACE_MATERIAL,
        road_builder.SHARED_STRUCTURE_MATERIAL,
    ] * 2,
    "slope": [
        road_builder.SHARED_SURFACE_MATERIAL,
        road_builder.SHARED_TUNNEL_MATERIAL,
    ] * 2,
    "tunnel": [
        road_builder.SHARED_SURFACE_MATERIAL,
        road_builder.SHARED_TUNNEL_MATERIAL,
    ] * 2,
}
expected_texture_paths = {
    road_builder.SHARED_SURFACE_MATERIAL: "textures/surface_d.png",
    road_builder.SHARED_STRUCTURE_MATERIAL: "textures/structure_d.png",
    road_builder.SHARED_TUNNEL_MATERIAL: "textures/tunnel_d.png",
}
expected_texture_families = {
    road_builder.SHARED_SURFACE_MATERIAL: "surface",
    road_builder.SHARED_STRUCTURE_MATERIAL: "structure",
    road_builder.SHARED_TUNNEL_MATERIAL: "tunnel",
}
for mode, entries in mode_entries.items():
    assert [entry["mesh"]["material"]["name"] for entry in entries] == expected_materials[mode]
    assert all(
        entry["mesh"]["material"]["main_texture_scale"] == [1.0, 0.5]
        for entry in entries
    ), mode
    for entry in entries:
        exported = entry["mesh"]
        if exported["material"]["name"] == road_builder.SHARED_SURFACE_MATERIAL:
            assert [round(value, 3) for value in exported["material"]["color"]] == [
                0.31, 0.32, 0.33, 1.0,
            ]
        assert exported["material"]["textures"] == [{
            "name": "_MainTex",
            "value_json": json.dumps(
                expected_texture_paths[exported["material"]["name"]],
            ),
        }]
        family = expected_texture_families[exported["material"]["name"]]
        assert exported["material"]["packed_textures"] == [
            {
                "name": "_APRMap",
                "packing": "APR",
                "sources": [
                    {
                        "name": map_id,
                        "value_json": json.dumps(f"textures/{family}_{map_id}.png"),
                    }
                    for map_id in ("a", "p", "r")
                ],
            },
            {
                "name": "_XYSMap",
                "packing": "XYS",
                "sources": [
                    {
                        "name": map_id,
                        "value_json": json.dumps(f"textures/{family}_{map_id}.png"),
                    }
                    for map_id in ("n", "s")
                ],
            },
        ]
        source_name = exported["name"].rsplit(".", 1)[0]
        source = bpy.data.objects[source_name]
        source_uv = {
            (round(float(item.uv.x), 7), round(float(item.uv.y), 7))
            for item in source.data.uv_layers["RoadUV"].data
        }
        exported_uv = {
            (round(exported["uv"][index], 7), round(exported["uv"][index + 1], 7))
            for index in range(0, len(exported["uv"]), 2)
        }
        assert exported_uv == source_uv, (mode, exported["name"])
    surface = entries[0]["mesh"]
    surface_indices = set(surface["triangles"])
    exported_heights = [surface["vertices"][index * 3 + 1] for index in surface_indices]
    assert round(min(exported_heights), 6) == -road_builder.ROADWAY_DEPRESSION, mode
    assert round(max(exported_heights), 6) == 0.0, mode
basic_segment = mode_entries["basic"][0]["mesh"]
assert len(basic_segment["vertices"]) == len(basic_segment["normals"])
assert len(basic_segment["uv"]) * 3 == len(basic_segment["vertices"]) * 2
assert len(basic_segment["triangles"]) > 0
assert basic_segment["material"]["shader"] == "Custom/Net/Road"
assert min(basic_segment["vertices"][2::3]) == -32.0
assert max(basic_segment["vertices"][2::3]) == 32.0
for entries in mode_entries.values():
    for entry in entries:
        mesh = entry["mesh"]
        vertices = mesh["vertices"]
        normals = mesh["normals"]
        triangles = mesh["triangles"]
        for offset in range(0, len(triangles), 3):
            indices = triangles[offset:offset + 3]
            points = [vertices[index * 3:index * 3 + 3] for index in indices]
            edge_ab = [points[1][axis] - points[0][axis] for axis in range(3)]
            edge_ac = [points[2][axis] - points[0][axis] for axis in range(3)]
            geometric_normal = (
                edge_ab[1] * edge_ac[2] - edge_ab[2] * edge_ac[1],
                edge_ab[2] * edge_ac[0] - edge_ab[0] * edge_ac[2],
                edge_ab[0] * edge_ac[1] - edge_ab[1] * edge_ac[0],
            )
            if sum(component * component for component in geometric_normal) <= 1e-12:
                continue
            exported_normal = [
                sum(normals[index * 3 + axis] for index in indices) / 3.0
                for axis in range(3)
            ]
            alignment = sum(
                geometric_normal[axis] * exported_normal[axis]
                for axis in range(3)
            )
            assert alignment > 1e-8, (mesh["name"], offset // 3, alignment)
props.runtime_road_id = "smoke-road-two"
props.runtime_prefab_name = "Smoke Road Two"
assert bpy.ops.cs1_road.export_runtime() == {"FINISHED"}
second_bundle = json.loads((runtime_output / "roads" / "smoke-road-two.json").read_text(encoding="utf-8"))
props.runtime_road_id = "smoke-road"
props.runtime_prefab_name = "Smoke Road"
edge_source = bpy.data.objects["ElevatedEdge"]
bpy.context.view_layer.objects.active = edge_source
edge_source.select_set(True)
props.runtime_prop_id = "smoke-decal"
assert bpy.ops.cs1_road.export_runtime_prop() == {"FINISHED"}
prop_bundle = json.loads((runtime_output / "props" / "smoke-decal.json").read_text(encoding="utf-8"))
assert prop_bundle["schema_version"] == 2
assert prop_bundle["prop_id"] == "smoke-decal"
assert prop_bundle["mesh"]["material"]["shader"] == "Custom/Props/Decal/Blend"
assert len(prop_bundle["mesh"]["triangles"]) > 0
previous_revision = bundle["revision"]
props.runtime_auto_export = True
props.depress_roadway = False
assert road_builder._runtime_auto_export_timer() == 1.0
updated_bundle = json.loads((runtime_output / "roads" / "smoke-road.json").read_text(encoding="utf-8"))
updated_manifest = json.loads((runtime_output / "manifest.json").read_text(encoding="utf-8"))
assert updated_bundle["revision"] != previous_revision
assert all(lane["vertical_offset"] == 0.0 for lane in updated_bundle["lanes"])
for mode in updated_bundle["modes"]:
    surface = mode["entries"][0]["mesh"]
    surface_indices = set(surface["triangles"])
    surface_heights = [surface["vertices"][index * 3 + 1] for index in surface_indices]
    assert round(min(surface_heights), 6) == 0.0, mode["mode"]
    assert round(max(surface_heights), 6) == 0.0, mode["mode"]
manifest_roads = {item["road_id"]: item for item in updated_manifest["roads"]}
assert len(manifest_roads) == 2
assert manifest_roads["smoke-road"]["revision"] == updated_bundle["revision"]
assert manifest_roads["smoke-road-two"]["revision"] == second_bundle["revision"]
props.runtime_auto_export = False

# Geometry controls throttle the selected mode and settle other generated modes.
base_span = (
    sum(lane.width for lane in props.lanes if lane.zone == "ROAD")
    + (props.median_width if props.median_enabled else 0.0)
    + 2.0
)
props.between_sidewalks_width = base_span
road_builder._LIVE_PREVIEW_PENDING.clear()
road_builder.build_mode(bpy.context.scene, "basic")
basic_before = bpy.data.objects["basic_segment"]
half_width_before = max(vertex.co.x for vertex in basic_before.data.vertices)
props.between_sidewalks_width = base_span + 0.2
scene_key = bpy.context.scene.as_pointer()
first_due = road_builder._LIVE_PREVIEW_PENDING[scene_key]["live_due"]
props.between_sidewalks_width = base_span + 0.4
assert len(road_builder._LIVE_PREVIEW_PENDING) == 1
pending = road_builder._LIVE_PREVIEW_PENDING[scene_key]
assert pending["live_due"] == first_due
assert pending["active_mode"] == "basic"
assert set(pending["modes"]) == {"basic", "elevated", "bridge", "slope", "tunnel"}
pending["live_due"] = 0.0
pending["settle_due"] = float("inf")
live_result = road_builder._live_preview_timer()
assert live_result is not None
half_width_after = max(vertex.co.x for vertex in bpy.data.objects["basic_segment"].data.vertices)
assert half_width_after > half_width_before
pending = road_builder._LIVE_PREVIEW_PENDING[scene_key]
assert pending["dirty"] is False
pending["settle_due"] = 0.0
assert road_builder._live_preview_timer() is None
props.between_sidewalks_width = base_span
road_builder._LIVE_PREVIEW_PENDING.clear()

assert bpy.ops.cs1_road.reload_surface_texture() == {"FINISHED"}

panel_layout = FakeLayout()
panel = type("FakePanel", (), {"layout": panel_layout})()
road_builder.CS1ROAD_PT_main.draw(panel, bpy.context)
assert panel_layout.property_names.count("mode") == 1
original_cross_section_sync = road_builder._sync_cross_section_state
def fail_if_panel_mutates_cross_section(_props):
    raise AssertionError("panel draw must not mutate cross-section state")
road_builder._sync_cross_section_state = fail_if_panel_mutates_cross_section
try:
    for panel_type in (
        road_builder.CS1ROAD_PT_shared,
        road_builder.CS1ROAD_PT_profiles,
        road_builder.CS1ROAD_PT_cross_section,
        road_builder.CS1ROAD_PT_markings,
        road_builder.CS1ROAD_PT_mode,
        road_builder.CS1ROAD_PT_files,
        road_builder.CS1ROAD_PT_runtime,
        road_builder.CS1ROAD_PT_development,
    ):
        panel_type.draw(panel, bpy.context)
finally:
    road_builder._sync_cross_section_state = original_cross_section_sync
assert "script.reload" in panel_layout.operator_ids
assert "cs1_road.reload_surface_texture" in panel_layout.operator_ids
assert "cs1_road.road_add" in panel_layout.operator_ids
assert "cs1_road.road_duplicate" in panel_layout.operator_ids
assert "cs1_road.road_remove" in panel_layout.operator_ids
assert "cs1_road.add_vehicle_variants" in panel_layout.operator_ids
assert "cs1_road.import_spec_new" in panel_layout.operator_ids
assert "cs1_road.export_all_specs" in panel_layout.operator_ids
assert "cs1_road.import_profiles" in panel_layout.operator_ids
assert "cs1_road.export_profiles" in panel_layout.operator_ids
assert "cs1_road.profile_assign_active" in panel_layout.operator_ids
assert "cs1_road.profile_override_toggle" in panel_layout.operator_ids
assert panel_layout.property_names.count("sidewalk_width") == 2
assert panel_layout.property_names.count("shoulder_width") == 0
assert "between_sidewalks_width" in panel_layout.property_names
assert "roadside_use" in panel_layout.property_names
assert "roadside_lines" in panel_layout.property_names
assert "lane_separator_style" in panel_layout.property_names
assert "center_line_style" in panel_layout.property_names
for median_field in (
    "median_profile", "median_width", "median_height",
):
    assert median_field in panel_layout.property_names
assert "zone" in panel_layout.property_names
assert "width" in panel_layout.property_names
assert "direction" in panel_layout.property_names
assert "vehicle_type" in panel_layout.property_names
assert "cs1_road.lane_override_toggle" in panel_layout.operator_ids
for hidden_field in (
    "lane_type", "vertical_offset",
    "node_shoulder_bands", "imt_center_line_yellow",
):
    assert hidden_field not in panel_layout.property_names
assert "node_transition_target" not in panel_layout.property_names
for panel_type in (
    road_builder.CS1ROAD_PT_profiles,
    road_builder.CS1ROAD_PT_markings,
    road_builder.CS1ROAD_PT_mode,
    road_builder.CS1ROAD_PT_files,
    road_builder.CS1ROAD_PT_runtime,
    road_builder.CS1ROAD_PT_development,
):
    assert "DEFAULT_CLOSED" in panel_type.bl_options

scene = bpy.context.scene
original_count = len(scene.cs1_roads)
source_index = scene.cs1_active_road_index
source_name = road_builder.active_road(scene).road_name
source_lane_ids = [lane.lane_id for lane in road_builder.active_road(scene).lanes]
assert bpy.ops.cs1_road.road_duplicate() == {"FINISHED"}
assert len(scene.cs1_roads) == original_count + 1
duplicate = road_builder.active_road(scene)
assert duplicate.road_name == f"{source_name} Copy"
assert [lane.lane_id for lane in duplicate.lanes] == source_lane_ids
assert duplicate.runtime_auto_export is False
first_duplicate_id = duplicate.runtime_road_id
first_duplicate_prefab = duplicate.runtime_prefab_name
scene.cs1_active_road_index = source_index
assert bpy.ops.cs1_road.road_duplicate() == {"FINISHED"}
second_duplicate = road_builder.active_road(scene)
assert len(scene.cs1_roads) == original_count + 2
assert second_duplicate.runtime_road_id != first_duplicate_id
assert second_duplicate.runtime_prefab_name != first_duplicate_prefab
assert bpy.ops.cs1_road.road_remove() == {"FINISHED"}
assert bpy.ops.cs1_road.road_remove() == {"FINISHED"}
assert len(scene.cs1_roads) == original_count
assert bpy.ops.cs1_road.road_add() == {"FINISHED"}
assert len(road_builder.active_road(scene).lanes) == 4
assert bpy.ops.cs1_road.road_remove() == {"FINISHED"}
assert len(scene.cs1_roads) == original_count
expected_variants = {
    item.road_id: item for item in road_builder.vehicle_lane_variants()
}
assert bpy.ops.cs1_road.add_vehicle_variants() == {"FINISHED"}
created_variants = {
    road.runtime_road_id: road
    for road in scene.cs1_roads
    if road.runtime_road_id in expected_variants
}
assert set(created_variants) == set(expected_variants)
for road_id, road in created_variants.items():
    variant = expected_variants[road_id]
    vehicle_lanes = [lane for lane in road.lanes if lane.lane_type == "VEHICLE"]
    assert len(vehicle_lanes) == variant.vehicle_lane_count
    assert sum(lane.direction == "BACKWARD" for lane in vehicle_lanes) == variant.backward_lanes
    assert sum(lane.direction == "FORWARD" for lane in vehicle_lanes) == variant.forward_lanes
    assert road.between_sidewalks_width == variant.vehicle_lane_count * 3.0 + 1.0
for index in range(len(scene.cs1_roads) - 1, -1, -1):
    if scene.cs1_roads[index].runtime_road_id in expected_variants:
        scene.cs1_roads.remove(index)
scene.cs1_active_road_index = min(source_index, len(scene.cs1_roads) - 1)
assert len(scene.cs1_roads) == original_count
assert bpy.ops.cs1_road.import_spec_new(filepath=str(spec_output)) == {"FINISHED"}
assert len(scene.cs1_roads) == original_count + 1
assert road_builder.active_road(scene).road_name == saved["name"]
assert bpy.ops.cs1_road.road_remove() == {"FINISHED"}
all_specs = ROOT / "build" / "smoke" / "all-road-specs"
if all_specs.exists():
    shutil.rmtree(all_specs)
assert bpy.ops.cs1_road.export_all_specs(directory=str(all_specs)) == {"FINISHED"}
assert len(list(all_specs.glob("*.json"))) == original_count + 1
profiles_data = json.loads((all_specs / "profiles.json").read_text(encoding="utf-8"))
assert profiles_data["schema_version"] == 1
assert any(item["id"] == props.profile_id for item in profiles_data["profiles"])
profile_file = ROOT / "build" / "smoke" / "profiles-roundtrip.json"
assert bpy.ops.cs1_road.export_profiles(filepath=str(profile_file)) == {"FINISHED"}
active_profile = road_builder._profile_for_road(scene, road_builder.active_road(scene))
profile_name = active_profile.name
active_profile.name = "Temporary profile name"
assert bpy.ops.cs1_road.import_profiles(filepath=str(profile_file)) == {"FINISHED"}
assert active_profile.name == profile_name
before_reload_count = len(scene.cs1_roads)
before_reload_name = road_builder.active_road(scene).road_name

# Match Blender's Reload Scripts lifecycle. The package must refresh its
# child modules and register cleanly again without restarting Blender.
road_builder.unregister()
road_builder = importlib.reload(road_builder)
road_builder.register()
road_builder._initialize_scene_lanes()
reloaded_props = road_builder.active_road(bpy.context.scene)
assert reloaded_props is not None
assert len(bpy.context.scene.cs1_roads) == before_reload_count
assert reloaded_props.road_name == before_reload_name
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
