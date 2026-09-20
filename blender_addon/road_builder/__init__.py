from __future__ import annotations

bl_info = {
    "name": "CS1 Road Builder",
    "author": "Local project",
    "version": (0, 4, 0),
    "blender": (5, 1, 0),
    "location": "3D View > Sidebar > Road",
    "description": "Edit and preview standalone Cities: Skylines 1 road modes",
    "category": "Object",
}

import json
from pathlib import Path

import bpy
from bpy.props import BoolProperty, CollectionProperty, EnumProperty, FloatProperty, IntProperty, PointerProperty, StringProperty
from bpy.types import Operator, Panel, PropertyGroup, UIList
from bpy_extras.io_utils import ExportHelper, ImportHelper


MODE_ITEMS = (
    ("basic", "Ground", "Ground road"),
    ("elevated", "Elevated", "Elevated road"),
    ("bridge", "Bridge", "Bridge road"),
    ("slope", "Tunnel Entrance", "Transition between ground and tunnel"),
    ("tunnel", "Tunnel", "Underground road"),
)
SURFACE_PROFILE_ITEMS = (
    ("FLUSH", "Ground level", "Road surface is level with surrounding ground"),
    ("DEPRESSED", "Curb depth", "Road surface is lower by the curb height"),
)
NODE_TRANSITION_ITEMS = (
    ("MATCH", "No transition", "Keep the node at the segment surface level"),
    ("FLUSH", "To ground level", "Slope the node surface toward ground level"),
    ("DEPRESSED", "To curb depth", "Slope the node surface toward curb depth"),
)
LANE_ZONE_ITEMS = (
    ("ROAD", "Roadway", "Lane occupies the roadway cross-section"),
    ("LEFT_SIDEWALK", "Left sidewalk", "Network lane lies on the left sidewalk"),
    ("RIGHT_SIDEWALK", "Right sidewalk", "Network lane lies on the right sidewalk"),
)
DIRECTION_ITEMS = (
    ("FORWARD", "Forward", "CS1 forward direction"),
    ("BACKWARD", "Backward", "CS1 backward direction"),
    ("BOTH", "Both", "Both directions"),
)
LANE_TYPE_ITEMS = (
    ("VEHICLE", "Vehicle", "Vehicle lane"),
    ("PEDESTRIAN", "Pedestrian", "Pedestrian lane"),
    ("TRANSPORT_VEHICLE", "Transport vehicle", "Public transport lane"),
    ("PARKING", "Parking", "Parking lane"),
    ("NONE", "None", "Metadata or prop lane such as a median"),
)
VEHICLE_TYPE_ITEMS = (
    ("NONE", "None", "No vehicle type"),
    ("CAR", "Car", "Car-compatible traffic"),
    ("BICYCLE", "Bicycle", "Bicycle traffic"),
    ("BUS", "Bus", "Bus traffic"),
    ("TRAM", "Tram", "Tram traffic"),
    ("TRAIN", "Train", "Train traffic"),
    ("METRO", "Metro", "Metro traffic"),
    ("MONORAIL", "Monorail", "Monorail traffic"),
    ("TROLLEYBUS", "Trolleybus", "Trolleybus traffic"),
)
BOUNDARY_ROLE_ITEMS = (
    ("CURB", "Curb", "Boundary between sidewalk and the road body"),
    ("CARRIAGEWAY_EDGE", "Carriageway edge", "Boundary between carriageway and shoulder"),
    ("LANE_DIVIDER", "Lane divider", "Boundary between adjacent carriageway strips"),
)
MARKING_ROLE_ITEMS = (
    ("CARRIAGEWAY_EDGE", "Roadside line", "Carriageway edge marking"),
    ("CENTER_LINE", "Center line", "Boundary between opposing traffic"),
    ("LANE_SEPARATOR", "Lane separator", "Boundary between lanes in the same direction"),
)
MARKING_STYLE_ITEMS = (
    ("SOLID_WHITE", "Solid white", "Shared solid white marking style"),
)
MODE_COLORS = {
    "basic": (0.12, 0.14, 0.16, 1.0),
    "elevated": (0.20, 0.25, 0.30, 1.0),
    "bridge": (0.24, 0.30, 0.36, 1.0),
    "slope": (0.18, 0.22, 0.27, 1.0),
    "tunnel": (0.10, 0.12, 0.15, 1.0),
}
MODE_LENGTH = 64.0
SEGMENT_SLICES = 20
NODE_SLICES = 8


def _enum_value(value: str, available, fallback: str) -> str:
    normalized = value.upper().replace(" ", "_")
    compact = normalized.replace("_", "")
    for key, _, _ in available:
        if key.replace("_", "") == compact:
            return key
    return fallback


def _mode_collection(scene: bpy.types.Scene, mode: str) -> bpy.types.Collection:
    root = bpy.data.collections.get("CS1_ROAD")
    if root is None:
        root = bpy.data.collections.new("CS1_ROAD")
        scene.collection.children.link(root)
    name = f"CS1_ROAD_{mode}"
    collection = bpy.data.collections.get(name)
    if collection is None:
        collection = bpy.data.collections.new(name)
        root.children.link(collection)
    return collection


def _clear_collection(collection: bpy.types.Collection) -> None:
    for obj in list(collection.objects):
        bpy.data.objects.remove(obj, do_unlink=True)


def _material(mode: str) -> bpy.types.Material:
    name = f"CS1 Road {mode.title()}"
    material = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    material.diffuse_color = MODE_COLORS[mode]
    return material


def _marking_material(mode: str, paint_width: float, region_width: float) -> bpy.types.Material:
    name = f"CS1 Road {mode.title()} Marking Preview"
    material = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    material.use_nodes = True
    nodes = material.node_tree.nodes
    links = material.node_tree.links
    nodes.clear()
    output = nodes.new("ShaderNodeOutputMaterial")
    shader = nodes.new("ShaderNodeBsdfPrincipled")
    texcoord = nodes.new("ShaderNodeTexCoord")
    separate = nodes.new("ShaderNodeSeparateXYZ")
    low = nodes.new("ShaderNodeMath")
    high = nodes.new("ShaderNodeMath")
    mask = nodes.new("ShaderNodeMath")
    mix = nodes.new("ShaderNodeMixRGB")
    low.operation, high.operation, mask.operation = "GREATER_THAN", "LESS_THAN", "MULTIPLY"
    half_ratio = min(0.49, paint_width / max(region_width, 0.001) * 0.5)
    low.inputs[1].default_value = 0.5 - half_ratio
    high.inputs[1].default_value = 0.5 + half_ratio
    mix.blend_type = "MIX"
    mix.inputs[1].default_value = MODE_COLORS[mode]
    mix.inputs[2].default_value = (0.92, 0.92, 0.88, 1.0)
    links.new(texcoord.outputs["UV"], separate.inputs[0])
    links.new(separate.outputs["X"], low.inputs[0])
    links.new(separate.outputs["X"], high.inputs[0])
    links.new(low.outputs[0], mask.inputs[0])
    links.new(high.outputs[0], mask.inputs[1])
    links.new(mask.outputs[0], mix.inputs[0])
    links.new(mix.outputs[0], shader.inputs["Base Color"])
    links.new(shader.outputs[0], output.inputs[0])
    return material


def _mesh_object(collection, name, vertices, faces, face_kinds, face_uvs, materials) -> bpy.types.Object:
    mesh = bpy.data.meshes.new(f"{name}_mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    uv_layer = mesh.uv_layers.new(name="RoadUV")
    mesh_y_min = min(vertex.co.y for vertex in mesh.vertices)
    mesh_y_max = max(vertex.co.y for vertex in mesh.vertices)
    for polygon, kind, explicit_uvs in zip(mesh.polygons, face_kinds, face_uvs):
        polygon.material_index = 1 if kind == "marking" else 0
        if explicit_uvs is not None:
            for loop_index, uv in zip(polygon.loop_indices, explicit_uvs):
                uv_layer.data[loop_index].uv = uv
            continue
        coordinates = [mesh.vertices[index].co for index in polygon.vertices]
        ranges = {
            "x": (min(value.x for value in coordinates), max(value.x for value in coordinates)),
            "y": (min(value.y for value in coordinates), max(value.y for value in coordinates)),
            "z": (min(value.z for value in coordinates), max(value.z for value in coordinates)),
        }
        if ranges["x"][1] - ranges["x"][0] > 1e-8 and ranges["y"][1] - ranges["y"][0] > 1e-8:
            axes = ("x", "y")
        elif ranges["y"][1] - ranges["y"][0] > 1e-8:
            axes = ("y", "z")
        else:
            axes = ("x", "z")
        for loop_index in polygon.loop_indices:
            coordinate = mesh.vertices[mesh.loops[loop_index].vertex_index].co
            first, second = axes
            if axes == ("x", "y"):
                u = (coordinate.x - ranges["x"][0]) / max(ranges["x"][1] - ranges["x"][0], 1e-8)
                v = (coordinate.y - mesh_y_min) / max(mesh_y_max - mesh_y_min, 1e-8)
            elif axes == ("y", "z"):
                u = (coordinate.y - mesh_y_min) / max(mesh_y_max - mesh_y_min, 1e-8)
                v = (coordinate.z - ranges["z"][0]) / max(ranges["z"][1] - ranges["z"][0], 1e-8)
            else:
                u = (getattr(coordinate, first) - ranges[first][0]) / max(ranges[first][1] - ranges[first][0], 1e-8)
                v = (getattr(coordinate, second) - ranges[second][0]) / max(ranges[second][1] - ranges[second][0], 1e-8)
            uv_layer.data[loop_index].uv = (u, v)
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    for material in materials:
        obj.data.materials.append(material)
    return obj


class _SurfaceMesh:
    """Build one welded open render mesh with optional intentional seams."""

    def __init__(self) -> None:
        self.vertices = []
        self.faces = []
        self.face_kinds = []
        self.face_uvs = []
        self._vertex_map = {}

    def _vertex(self, coordinate, seam="") -> int:
        key = (seam, tuple(coordinate))
        if key not in self._vertex_map:
            self._vertex_map[key] = len(self.vertices)
            self.vertices.append(tuple(coordinate))
        return self._vertex_map[key]

    def quad(self, a, b, c, d, seams=("", "", "", ""), kind="surface", uvs=None) -> None:
        self.faces.append(tuple(self._vertex(point, seam) for point, seam in zip((a, b, c, d), seams)))
        self.face_kinds.append(kind)
        self.face_uvs.append(tuple(uvs) if uvs is not None else None)

    def horizontal_strip(self, x_min, x_max, y_min, y_max, start_z, end_z, slices=1, flip=False) -> None:
        for index in range(slices):
            t0, t1 = index / slices, (index + 1) / slices
            ya, yb = y_min + (y_max - y_min) * t0, y_min + (y_max - y_min) * t1
            za, zb = start_z + (end_z - start_z) * t0, start_z + (end_z - start_z) * t1
            points = ((x_min, ya, za), (x_max, ya, za), (x_max, yb, zb), (x_min, yb, zb))
            self.quad(*tuple(reversed(points)) if flip else points)

    def vertical_strip(self, x, y_min, y_max, start_bottom, end_bottom, start_top, end_top, flip=False, slices=1) -> None:
        for index in range(slices):
            t0, t1 = index / slices, (index + 1) / slices
            ya, yb = y_min + (y_max - y_min) * t0, y_min + (y_max - y_min) * t1
            ba, bb = start_bottom + (end_bottom - start_bottom) * t0, start_bottom + (end_bottom - start_bottom) * t1
            ta, tb = start_top + (end_top - start_top) * t0, start_top + (end_top - start_top) * t1
            points = ((x, ya, ba), (x, yb, bb), (x, yb, tb), (x, ya, ta))
            self.quad(*tuple(reversed(points)) if flip else points)

    def create(self, collection, name, materials) -> bpy.types.Object:
        return _mesh_object(collection, name, self.vertices, self.faces, self.face_kinds, self.face_uvs, materials)


def _marking_layout(props) -> tuple[list[float], list[float]]:
    _sync_boundaries(props)
    road_lanes = [lane for lane in props.lanes if lane.zone == "ROAD"]
    lane_width = sum(lane.width for lane in road_lanes)
    cursor = -lane_width * 0.5
    positions = {}
    if road_lanes:
        left_id = "boundary-left-carriageway" if props.shoulder_width > 1e-8 else "boundary-left-curb"
        positions[left_id] = cursor
    for left, right in zip(road_lanes, road_lanes[1:]):
        cursor += left.width
        positions[f"boundary-{left.lane_id}-{right.lane_id}"] = cursor
    if road_lanes:
        right_id = "boundary-right-carriageway" if props.shoulder_width > 1e-8 else "boundary-right-curb"
        positions[right_id] = lane_width * 0.5
    enabled_centers = [positions[item.boundary_id] for item in props.boundaries if item.marking_enabled and item.boundary_id in positions]
    edge_centers = [positions[key] for key in positions if key in {"boundary-left-carriageway", "boundary-left-curb", "boundary-right-carriageway", "boundary-right-curb"}]
    return enabled_centers, edge_centers


def _segment_boundaries(road_half, marking_centers, marking_region_width) -> list[float]:
    values = [-road_half, road_half]
    half_region = marking_region_width * 0.5
    for center in marking_centers:
        values.extend((max(-road_half, center - half_region), min(road_half, center + half_region)))
    return sorted({round(value, 9) for value in values})


def _node_boundaries(props, road_half, edge_centers) -> list[float]:
    values = [-road_half, 0.0, road_half]
    if props.node_shoulder_bands:
        values.extend(edge_centers)
    return sorted({round(value, 9) for value in values})


def _cross_section_uv_row(t, road_half, total_half, road_start, road_end, sidewalk_start, sidewalk_end):
    road_z = road_start + (road_end - road_start) * t
    sidewalk_z = sidewalk_start + (sidewalk_end - sidewalk_start) * t
    sidewalk_width = total_half - road_half
    rise = abs(sidewalk_z - road_z)
    unfolded_width = 2.0 * sidewalk_width + 2.0 * rise + 2.0 * road_half
    left_top = sidewalk_width / unfolded_width
    left_bottom = (sidewalk_width + rise) / unfolded_width
    right_bottom = (sidewalk_width + rise + 2.0 * road_half) / unfolded_width
    right_top = (sidewalk_width + 2.0 * rise + 2.0 * road_half) / unfolded_width
    return {
        "road_z": road_z,
        "sidewalk_z": sidewalk_z,
        "left_top": left_top,
        "left_bottom": left_bottom,
        "right_bottom": right_bottom,
        "right_top": right_top,
        "road_u": lambda x: (sidewalk_width + rise + x + road_half) / unfolded_width,
    }


def _add_road_strips(
    mesh, boundaries, marking_centers, marking_width, road_half, total_half, y_min, y_max,
    road_start, road_end, sidewalk_start, sidewalk_end, split_center, slices,
) -> None:
    for x_min, x_max in zip(boundaries, boundaries[1:]):
        if x_max - x_min < 1e-8:
            continue
        for index in range(slices):
            t0, t1 = index / slices, (index + 1) / slices
            ya, yb = y_min + (y_max - y_min) * t0, y_min + (y_max - y_min) * t1
            row0 = _cross_section_uv_row(t0, road_half, total_half, road_start, road_end, sidewalk_start, sidewalk_end)
            row1 = _cross_section_uv_row(t1, road_half, total_half, road_start, road_end, sidewalk_start, sidewalk_end)
            za, zb = row0["road_z"], row1["road_z"]
            seams = ["", "", "", ""]
            if split_center and abs(x_max) < 1e-8:
                seams[1] = seams[2] = "center_left"
            if split_center and abs(x_min) < 1e-8:
                seams[0] = seams[3] = "center_right"
            midpoint = (x_min + x_max) * 0.5
            kind = "marking" if any(abs(midpoint - center) <= marking_width * 0.5 + 1e-8 for center in marking_centers) else "surface"
            if kind == "marking":
                uvs = ((0.0, t0), (1.0, t0), (1.0, t1), (0.0, t1))
            else:
                uvs = (
                    (row0["road_u"](x_min), t0), (row0["road_u"](x_max), t0),
                    (row1["road_u"](x_max), t1), (row1["road_u"](x_min), t1),
                )
            mesh.quad((x_min, ya, za), (x_max, ya, za), (x_max, yb, zb), (x_min, yb, zb), seams, kind, uvs)


def _add_cross_section_top(
    mesh, boundaries, marking_centers, marking_width, road_half, total_half, y_min, y_max,
    road_start, road_end, sidewalk_start, sidewalk_end, split_center, slices,
) -> None:
    _add_road_strips(
        mesh, boundaries, marking_centers, marking_width, road_half, total_half, y_min, y_max,
        road_start, road_end, sidewalk_start, sidewalk_end, split_center, slices,
    )
    for index in range(slices):
        t0, t1 = index / slices, (index + 1) / slices
        ya, yb = y_min + (y_max - y_min) * t0, y_min + (y_max - y_min) * t1
        row0 = _cross_section_uv_row(t0, road_half, total_half, road_start, road_end, sidewalk_start, sidewalk_end)
        row1 = _cross_section_uv_row(t1, road_half, total_half, road_start, road_end, sidewalk_start, sidewalk_end)
        mesh.quad(
            (-total_half, ya, row0["sidewalk_z"]), (-road_half, ya, row0["sidewalk_z"]),
            (-road_half, yb, row1["sidewalk_z"]), (-total_half, yb, row1["sidewalk_z"]),
            uvs=((0.0, t0), (row0["left_top"], t0), (row1["left_top"], t1), (0.0, t1)),
        )
        mesh.quad(
            (road_half, ya, row0["sidewalk_z"]), (total_half, ya, row0["sidewalk_z"]),
            (total_half, yb, row1["sidewalk_z"]), (road_half, yb, row1["sidewalk_z"]),
            uvs=((row0["right_top"], t0), (1.0, t0), (1.0, t1), (row1["right_top"], t1)),
        )
        if row0["road_z"] != row0["sidewalk_z"] or row1["road_z"] != row1["sidewalk_z"]:
            mesh.quad(
                (-road_half, ya, row0["road_z"]), (-road_half, yb, row1["road_z"]),
                (-road_half, yb, row1["sidewalk_z"]), (-road_half, ya, row0["sidewalk_z"]),
                uvs=(
                    (row0["left_bottom"], t0), (row1["left_bottom"], t1),
                    (row1["left_top"], t1), (row0["left_top"], t0),
                ),
            )
            mesh.quad(
                (road_half, ya, row0["sidewalk_z"]), (road_half, yb, row1["sidewalk_z"]),
                (road_half, yb, row1["road_z"]), (road_half, ya, row0["road_z"]),
                uvs=(
                    (row0["right_top"], t0), (row1["right_top"], t1),
                    (row1["right_bottom"], t1), (row0["right_bottom"], t0),
                ),
            )


def _add_deck_structure(mesh, half_width, y_min, y_max, start_z, end_z, depth, slices) -> None:
    mesh.horizontal_strip(-half_width, half_width, y_min, y_max, start_z - depth, end_z - depth, slices, flip=True)
    mesh.vertical_strip(-half_width, y_min, y_max, start_z - depth, end_z - depth, start_z, end_z, flip=True, slices=slices)
    mesh.vertical_strip(half_width, y_min, y_max, start_z - depth, end_z - depth, start_z, end_z, slices=slices)


def _add_tunnel_envelope(mesh, half_width, y_min, y_max, side_z, roof_z, slices) -> None:
    mesh.vertical_strip(-half_width, y_min, y_max, side_z, side_z, roof_z, roof_z, slices=slices)
    mesh.vertical_strip(half_width, y_min, y_max, side_z, side_z, roof_z, roof_z, flip=True, slices=slices)
    mesh.horizontal_strip(-half_width, half_width, y_min, y_max, roof_z, roof_z, slices, flip=True)


def _add_default_lanes(props) -> None:
    defaults = (
        ("lane-left-sidewalk", "Left sidewalk", "LEFT_SIDEWALK", 2.0, "BOTH", "PEDESTRIAN", "NONE", 0.1),
        ("lane-backward-1", "Backward 1", "ROAD", 3.0, "BACKWARD", "VEHICLE", "CAR", 1.0),
        ("lane-forward-1", "Forward 1", "ROAD", 3.0, "FORWARD", "VEHICLE", "CAR", 1.0),
        ("lane-right-sidewalk", "Right sidewalk", "RIGHT_SIDEWALK", 2.0, "BOTH", "PEDESTRIAN", "NONE", 0.1),
    )
    for lane_id, name, zone, width, direction, lane_type, vehicle_type, speed in defaults:
        lane = props.lanes.add()
        lane.lane_id = lane_id
        lane.name, lane.zone, lane.width = name, zone, width
        lane.direction, lane.lane_type, lane.vehicle_type = direction, lane_type, vehicle_type
        lane.speed_limit = speed


def _ensure_default_lanes(props) -> None:
    if not props.lanes:
        _add_default_lanes(props)
    _ensure_lane_ids(props)
    _sync_boundaries(props)


def _ensure_lane_ids(props) -> None:
    used = set()
    next_id = max(1, props.next_lane_id)
    for lane in props.lanes:
        lane_id = lane.lane_id.strip()
        if not lane_id or lane_id in used:
            while f"lane-{next_id}" in used:
                next_id += 1
            lane_id = f"lane-{next_id}"
            next_id += 1
            lane.lane_id = lane_id
        used.add(lane_id)
    props.next_lane_id = next_id


def _strip_id(lane) -> str:
    if lane.zone == "LEFT_SIDEWALK":
        return "strip-left-sidewalk"
    if lane.zone == "RIGHT_SIDEWALK":
        return "strip-right-sidewalk"
    return f"strip-{lane.lane_id}"


def _expected_boundaries(props):
    road_lanes = [lane for lane in props.lanes if lane.zone == "ROAD"]
    if not road_lanes:
        return []
    expected = []
    left_road_strip, right_road_strip = _strip_id(road_lanes[0]), _strip_id(road_lanes[-1])
    if props.shoulder_width > 1e-8:
        expected.extend((
            ("boundary-left-curb", "Left curb", "CURB", "strip-left-sidewalk", "strip-left-shoulder", False, "CARRIAGEWAY_EDGE"),
            ("boundary-left-carriageway", "Left roadside line", "CARRIAGEWAY_EDGE", "strip-left-shoulder", left_road_strip, True, "CARRIAGEWAY_EDGE"),
        ))
    else:
        expected.append((
            "boundary-left-curb", "Left curb / roadside", "CURB", "strip-left-sidewalk", left_road_strip, True, "CARRIAGEWAY_EDGE",
        ))
    for left, right in zip(road_lanes, road_lanes[1:]):
        marking_role = "CENTER_LINE" if left.direction != right.direction else "LANE_SEPARATOR"
        expected.append((
            f"boundary-{left.lane_id}-{right.lane_id}", f"{left.name} / {right.name}", "LANE_DIVIDER",
            _strip_id(left), _strip_id(right), True, marking_role,
        ))
    if props.shoulder_width > 1e-8:
        expected.extend((
            ("boundary-right-carriageway", "Right roadside line", "CARRIAGEWAY_EDGE", right_road_strip, "strip-right-shoulder", True, "CARRIAGEWAY_EDGE"),
            ("boundary-right-curb", "Right curb", "CURB", "strip-right-shoulder", "strip-right-sidewalk", False, "CARRIAGEWAY_EDGE"),
        ))
    else:
        expected.append((
            "boundary-right-curb", "Right curb / roadside", "CURB", right_road_strip, "strip-right-sidewalk", True, "CARRIAGEWAY_EDGE",
        ))
    return expected


def _sync_boundaries(props, legacy_edge_lines=None, legacy_lane_lines=None) -> None:
    _ensure_lane_ids(props)
    previous = {
        item.boundary_id: {
            "boundary_role": item.role,
            "enabled": item.marking_enabled,
            "role": item.marking_role,
            "style": item.marking_style,
        }
        for item in props.boundaries
    }
    props.boundaries.clear()
    for boundary_id, name, role, left_strip, right_strip, default_enabled, default_marking_role in _expected_boundaries(props):
        item = props.boundaries.add()
        item.boundary_id, item.name = boundary_id, name
        item.left_strip_id, item.right_strip_id = left_strip, right_strip
        saved = previous.get(boundary_id)
        item.role = saved["boundary_role"] if saved else role
        item.marking_enabled = saved["enabled"] if saved else default_enabled
        item.marking_role = saved["role"] if saved else default_marking_role
        item.marking_style = saved["style"] if saved else "SOLID_WHITE"
        if legacy_edge_lines is not None and item.marking_role == "CARRIAGEWAY_EDGE":
            item.marking_enabled = bool(legacy_edge_lines)
        if legacy_lane_lines is not None and item.role == "LANE_DIVIDER":
            item.marking_enabled = bool(legacy_lane_lines)


def _initialize_scene_lanes():
    """Initialize defaults after Blender leaves its restricted register context."""
    try:
        scenes = list(bpy.data.scenes)
    except AttributeError:
        return 0.1
    for scene in scenes:
        _ensure_default_lanes(scene.cs1_road_builder)
    return None


def _road_lane_width(props) -> float:
    return max(sum(lane.width for lane in props.lanes if lane.zone == "ROAD"), 0.01)


def _cross_section(props) -> tuple[float, float, float]:
    roadway_width = _road_lane_width(props) + 2.0 * props.shoulder_width
    total_width = roadway_width + 2.0 * props.sidewalk_width
    return roadway_width, total_width, total_width * 0.5


def _lane_positions(props):
    roadway_lanes = [lane for lane in props.lanes if lane.zone == "ROAD"]
    cursor = -sum(lane.width for lane in roadway_lanes) * 0.5
    positions = {}
    for lane in roadway_lanes:
        positions[lane.as_pointer()] = cursor + lane.width * 0.5
        cursor += lane.width
    roadway_width, _, total_half = _cross_section(props)
    for zone, start in (("LEFT_SIDEWALK", -total_half), ("RIGHT_SIDEWALK", roadway_width * 0.5)):
        cursor = start
        for lane in (item for item in props.lanes if item.zone == zone):
            positions[lane.as_pointer()] = cursor + lane.width * 0.5
            cursor += lane.width
    return [(lane, positions.get(lane.as_pointer(), 0.0)) for lane in props.lanes]


def build_mode(scene: bpy.types.Scene, mode: str) -> list[bpy.types.Object]:
    props = scene.cs1_road_builder
    collection = _mode_collection(scene, mode)
    _clear_collection(collection)
    material = _material(mode)
    marking_material = _marking_material(mode, props.marking_paint_width, props.marking_region_width)
    roadway_width, _, total_half = _cross_section(props)
    road_half = roadway_width * 0.5
    segment_markings, edge_centers = _marking_layout(props)
    segment_boundaries = _segment_boundaries(road_half, segment_markings, props.marking_region_width)
    node_boundaries = _node_boundaries(props, road_half, edge_centers)
    node_markings = []
    curb_rise = props.curb_height if props.surface_profile == "DEPRESSED" else 0.0
    props.half_width = total_half
    y_min, y_max = -MODE_LENGTH * 0.5, MODE_LENGTH * 0.5
    segment_mesh, node_mesh = _SurfaceMesh(), _SurfaceMesh()

    if mode == "basic":
        segment_z = 0.0 if props.surface_profile == "FLUSH" else -props.curb_height
        target = props.surface_profile if props.node_transition_target == "MATCH" else props.node_transition_target
        far_z = 0.0 if target == "FLUSH" else -props.curb_height
        _add_cross_section_top(segment_mesh, segment_boundaries, segment_markings, props.marking_region_width, road_half, total_half, y_min, y_max, segment_z, segment_z, 0.0, 0.0, False, SEGMENT_SLICES)
        _add_cross_section_top(node_mesh, node_boundaries, node_markings, props.marking_region_width, road_half, total_half, y_min, y_max, segment_z, far_z, 0.0, 0.0, True, NODE_SLICES)
    elif mode == "elevated":
        road_z, side_z = props.elevated_height, props.elevated_height + curb_rise
        _add_cross_section_top(segment_mesh, segment_boundaries, segment_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_z, road_z, side_z, side_z, False, SEGMENT_SLICES)
        _add_cross_section_top(node_mesh, node_boundaries, node_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_z, road_z, side_z, side_z, True, NODE_SLICES)
        _add_deck_structure(segment_mesh, total_half, y_min, y_max, side_z, side_z, props.deck_depth, SEGMENT_SLICES)
        _add_deck_structure(node_mesh, total_half, y_min, y_max, side_z, side_z, props.deck_depth, NODE_SLICES)
    elif mode == "bridge":
        road_z, side_z = props.bridge_height, props.bridge_height + curb_rise
        _add_cross_section_top(segment_mesh, segment_boundaries, segment_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_z, road_z, side_z, side_z, False, SEGMENT_SLICES)
        _add_cross_section_top(node_mesh, node_boundaries, node_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_z, road_z, side_z, side_z, True, NODE_SLICES)
        _add_deck_structure(segment_mesh, total_half, y_min, y_max, side_z, side_z, props.bridge_deck_depth, SEGMENT_SLICES)
        _add_deck_structure(node_mesh, total_half, y_min, y_max, side_z, side_z, props.bridge_deck_depth, NODE_SLICES)
    elif mode == "slope":
        road_start, road_end = 0.0, -props.tunnel_depth
        side_start, side_end = road_start + curb_rise, road_end + curb_rise
        _add_cross_section_top(segment_mesh, segment_boundaries, segment_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_start, road_end, side_start, side_end, False, SEGMENT_SLICES)
        _add_cross_section_top(node_mesh, node_boundaries, node_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_end, road_end, side_end, side_end, True, NODE_SLICES)
        _add_deck_structure(segment_mesh, total_half, y_min, y_max, side_start, side_end, props.deck_depth, SEGMENT_SLICES)
        _add_deck_structure(node_mesh, total_half, y_min, y_max, side_end, side_end, props.deck_depth, NODE_SLICES)
    else:
        road_z = -props.tunnel_depth
        side_z, roof_z = road_z + curb_rise, road_z + props.tunnel_clearance
        _add_cross_section_top(segment_mesh, segment_boundaries, segment_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_z, road_z, side_z, side_z, False, SEGMENT_SLICES)
        _add_cross_section_top(node_mesh, node_boundaries, node_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_z, road_z, side_z, side_z, True, NODE_SLICES)
        _add_tunnel_envelope(segment_mesh, total_half, y_min, y_max, side_z, roof_z, SEGMENT_SLICES)
        _add_tunnel_envelope(node_mesh, total_half, y_min, y_max, side_z, roof_z, NODE_SLICES)

    segment = segment_mesh.create(collection, f"{mode}_segment", (material, marking_material))
    node = node_mesh.create(collection, f"{mode}_node", (material,))
    segment.location.y, node.location.y = -MODE_LENGTH * 0.5, MODE_LENGTH * 0.5
    for obj, part in ((segment, "segment"), (node, "node")):
        obj["cs1_road_mode"], obj["cs1_road_name"], obj["cs1_road_part"] = mode, props.road_name, part
        obj["cs1_local_length"] = MODE_LENGTH
        obj["cs1_longitudinal_slices"] = SEGMENT_SLICES if part == "segment" else NODE_SLICES
    node["cs1_center_split"] = True
    return [segment, node]


class CS1RoadLane(PropertyGroup):
    lane_id: StringProperty(name="Lane ID")
    zone: EnumProperty(name="Zone", items=LANE_ZONE_ITEMS, default="ROAD")
    width: FloatProperty(name="Width", default=3.0, min=0.05, max=16.0, unit="LENGTH")
    direction: EnumProperty(name="Direction", items=DIRECTION_ITEMS, default="FORWARD")
    lane_type: EnumProperty(name="Lane type", items=LANE_TYPE_ITEMS, default="VEHICLE")
    vehicle_type: EnumProperty(name="Vehicle type", items=VEHICLE_TYPE_ITEMS, default="CAR")
    speed_limit: FloatProperty(name="Speed limit", default=1.0, min=0.0, max=10.0)
    vertical_offset: FloatProperty(name="Vertical offset", default=0.0, min=-16.0, max=16.0, unit="LENGTH")
    stop_offset: FloatProperty(name="Stop offset", default=0.0, min=-32.0, max=32.0, unit="LENGTH")
    allow_connect: BoolProperty(name="Allow connect", default=True)


class CS1RoadBoundary(PropertyGroup):
    boundary_id: StringProperty(name="Boundary ID")
    left_strip_id: StringProperty(name="Left strip ID")
    right_strip_id: StringProperty(name="Right strip ID")
    role: EnumProperty(name="Boundary role", items=BOUNDARY_ROLE_ITEMS, default="LANE_DIVIDER")
    marking_enabled: BoolProperty(name="Marking", default=False)
    marking_role: EnumProperty(name="Marking role", items=MARKING_ROLE_ITEMS, default="LANE_SEPARATOR")
    marking_style: EnumProperty(name="Marking style", items=MARKING_STYLE_ITEMS, default="SOLID_WHITE")


class CS1RoadBuilderProperties(PropertyGroup):
    road_name: StringProperty(name="Road name", default="Example Road")
    mode: EnumProperty(name="Mode", items=MODE_ITEMS, default="basic")
    lanes: CollectionProperty(type=CS1RoadLane)
    active_lane_index: IntProperty(default=0, min=0)
    next_lane_id: IntProperty(default=1, min=1)
    boundaries: CollectionProperty(type=CS1RoadBoundary)
    active_boundary_index: IntProperty(default=0, min=0)
    half_width: FloatProperty(name="Half width", default=5.75, min=0.01, max=128.0, unit="LENGTH")
    shoulder_width: FloatProperty(name="Shoulder width", default=0.5, min=0.0, max=8.0, unit="LENGTH")
    sidewalk_width: FloatProperty(name="Sidewalk width", default=2.5, min=0.0, max=16.0, unit="LENGTH")
    curb_height: FloatProperty(name="Curb height", default=0.15, min=0.0, max=1.0, unit="LENGTH")
    edge_lines: BoolProperty(name="Legacy roadside lines", default=True, options={"HIDDEN"})
    lane_lines: BoolProperty(name="Legacy lane divider lines", default=True, options={"HIDDEN"})
    marking_paint_width: FloatProperty(name="Paint width", default=0.15, min=0.05, max=0.5, unit="LENGTH")
    marking_region_width: FloatProperty(name="Marking region", default=0.4, min=0.2, max=1.0, unit="LENGTH")
    node_shoulder_bands: BoolProperty(name="Node shoulder bands", default=False)
    surface_profile: EnumProperty(name="Segment surface", items=SURFACE_PROFILE_ITEMS, default="DEPRESSED")
    node_transition_target: EnumProperty(name="Transition target", items=NODE_TRANSITION_ITEMS, default="MATCH")
    elevated_height: FloatProperty(name="Elevated height", default=8.0, min=1.0, max=64.0, unit="LENGTH")
    bridge_height: FloatProperty(name="Bridge height", default=12.0, min=1.0, max=128.0, unit="LENGTH")
    deck_depth: FloatProperty(name="Elevated deck depth", default=1.0, min=0.1, max=8.0, unit="LENGTH")
    bridge_deck_depth: FloatProperty(name="Bridge deck depth", default=1.5, min=0.1, max=12.0, unit="LENGTH")
    tunnel_depth: FloatProperty(name="Tunnel depth", default=12.0, min=1.0, max=64.0, unit="LENGTH")
    tunnel_clearance: FloatProperty(name="Tunnel clearance", default=5.0, min=2.0, max=16.0, unit="LENGTH")
    hide_other_modes: BoolProperty(name="Hide other modes", default=True)


class CS1ROAD_UL_lanes(UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_propname, index):
        if self.layout_type in {"DEFAULT", "COMPACT"}:
            row = layout.row(align=True)
            row.label(text=str(index + 1))
            row.prop(item, "direction", text="")
            row.prop(item, "lane_type", text="")
            row.prop(item, "width", text="")
        else:
            layout.label(text=str(index + 1))


class CS1ROAD_UL_boundaries(UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_propname, index):
        if self.layout_type in {"DEFAULT", "COMPACT"}:
            row = layout.row(align=True)
            row.prop(item, "marking_enabled", text="")
            row.label(text=item.name or item.boundary_id)
            row.label(text=item.role.replace("_", " ").title())
        else:
            layout.label(text=item.name or item.boundary_id)


class CS1ROAD_OT_lane_add(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.lane_add", "Add lane", {"REGISTER", "UNDO"}

    def execute(self, context):
        props = context.scene.cs1_road_builder
        lane = props.lanes.add()
        _ensure_lane_ids(props)
        lane.name = f"Lane {len(props.lanes)}"
        props.active_lane_index = len(props.lanes) - 1
        _sync_boundaries(props)
        return {"FINISHED"}


class CS1ROAD_OT_lanes_reset(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.lanes_reset", "Create default lanes", {"REGISTER", "UNDO"}

    def execute(self, context):
        props = context.scene.cs1_road_builder
        props.lanes.clear()
        _add_default_lanes(props)
        props.active_lane_index = 0
        _sync_boundaries(props)
        return {"FINISHED"}


class CS1ROAD_OT_lane_remove(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.lane_remove", "Remove lane", {"REGISTER", "UNDO"}

    def execute(self, context):
        props = context.scene.cs1_road_builder
        if props.lanes:
            props.lanes.remove(min(props.active_lane_index, len(props.lanes) - 1))
            props.active_lane_index = max(0, min(props.active_lane_index, len(props.lanes) - 1))
            _sync_boundaries(props)
        return {"FINISHED"}


class CS1ROAD_OT_lane_move(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.lane_move", "Move lane", {"REGISTER", "UNDO"}
    direction: EnumProperty(items=(("UP", "Up", ""), ("DOWN", "Down", "")))

    def execute(self, context):
        props = context.scene.cs1_road_builder
        source = props.active_lane_index
        target = source - 1 if self.direction == "UP" else source + 1
        if 0 <= source < len(props.lanes) and 0 <= target < len(props.lanes):
            props.lanes.move(source, target)
            props.active_lane_index = target
            _sync_boundaries(props)
        return {"FINISHED"}


class CS1ROAD_OT_boundaries_sync(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.boundaries_sync", "Synchronize boundaries", {"REGISTER", "UNDO"}

    def execute(self, context):
        _sync_boundaries(context.scene.cs1_road_builder)
        return {"FINISHED"}


def _show_only_mode(active_mode: str) -> None:
    for mode, _, _ in MODE_ITEMS:
        collection = bpy.data.collections.get(f"CS1_ROAD_{mode}")
        if collection is not None:
            collection.hide_viewport = collection.hide_render = mode != active_mode


class CS1ROAD_OT_build_preview(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.build_preview", "Build active mode", {"REGISTER", "UNDO"}

    def execute(self, context):
        props = context.scene.cs1_road_builder
        build_mode(context.scene, props.mode)
        if props.hide_other_modes:
            _show_only_mode(props.mode)
        return {"FINISHED"}


class CS1ROAD_OT_build_all(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.build_all", "Build all modes", {"REGISTER", "UNDO"}

    def execute(self, context):
        active = context.scene.cs1_road_builder.mode
        for mode, _, _ in MODE_ITEMS:
            build_mode(context.scene, mode)
        _show_only_mode(active)
        return {"FINISHED"}


class CS1ROAD_OT_show_mode(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.show_mode", "Show active mode", {"REGISTER", "UNDO"}

    def execute(self, context):
        _show_only_mode(context.scene.cs1_road_builder.mode)
        return {"FINISHED"}


def _load_lane(target, source) -> None:
    target.lane_id = str(source.get("id", source.get("lane_id", "")))
    target.name = str(source.get("name", "Lane"))
    target.zone = _enum_value(str(source.get("zone", "ROAD")), LANE_ZONE_ITEMS, "ROAD")
    target.width = float(source.get("width", target.width))
    target.direction = _enum_value(str(source.get("direction", "FORWARD")), DIRECTION_ITEMS, "FORWARD")
    target.lane_type = _enum_value(str(source.get("lane_type", "VEHICLE")), LANE_TYPE_ITEMS, "VEHICLE")
    target.vehicle_type = _enum_value(str(source.get("vehicle_type", "CAR")), VEHICLE_TYPE_ITEMS, "CAR")
    target.speed_limit = float(source.get("speed_limit", target.speed_limit))
    target.vertical_offset = float(source.get("vertical_offset", target.vertical_offset))
    target.stop_offset = float(source.get("stop_offset", target.stop_offset))
    target.allow_connect = bool(source.get("allow_connect", target.allow_connect))


def _layout_strips(props):
    strips = [{"id": "strip-left-sidewalk", "function": "SIDEWALK", "width": props.sidewalk_width, "surface_style": "SIDEWALK"}]
    if props.shoulder_width > 1e-8:
        strips.append({"id": "strip-left-shoulder", "function": "SHOULDER", "width": props.shoulder_width, "surface_style": "ASPHALT"})
    for lane in (item for item in props.lanes if item.zone == "ROAD"):
        strips.append({"id": _strip_id(lane), "function": "CARRIAGEWAY", "width": lane.width, "surface_style": "ASPHALT"})
    if props.shoulder_width > 1e-8:
        strips.append({"id": "strip-right-shoulder", "function": "SHOULDER", "width": props.shoulder_width, "surface_style": "ASPHALT"})
    strips.append({"id": "strip-right-sidewalk", "function": "SIDEWALK", "width": props.sidewalk_width, "surface_style": "SIDEWALK"})
    return strips


def _export_lanes(props):
    offsets = {"strip-left-sidewalk": 0.0, "strip-right-sidewalk": 0.0}
    lanes = []
    for lane in props.lanes:
        strip_id = _strip_id(lane)
        start = 0.0 if lane.zone == "ROAD" else offsets[strip_id]
        end = start + lane.width
        if lane.zone != "ROAD":
            offsets[strip_id] = end
        lanes.append({
            "id": lane.lane_id, "name": lane.name, "zone": lane.zone,
            "surface_strip_id": strip_id, "lateral_start": start, "lateral_end": end,
            "vertical_offset": lane.vertical_offset, "stop_offset": lane.stop_offset,
            "speed_limit": lane.speed_limit, "direction": lane.direction,
            "lane_type": lane.lane_type, "vehicle_type": lane.vehicle_type,
            "allow_connect": lane.allow_connect,
        })
    return lanes


def _export_layout(props):
    _sync_boundaries(props)
    strips = _layout_strips(props)
    boundaries = []
    for item in props.boundaries:
        marking = None
        if item.marking_enabled:
            marking = {
                "role": item.marking_role,
                "style_id": item.marking_style,
                "placement": "CENTER",
            }
        boundaries.append({
            "id": item.boundary_id, "role": item.role,
            "left_strip_id": item.left_strip_id, "right_strip_id": item.right_strip_id,
            "profile_id": "CURB" if item.role == "CURB" else "FLAT",
            "marking": marking,
        })
    total_width = sum(strip["width"] for strip in strips)
    return {"alignment_offset_from_left": total_width * 0.5, "strips": strips, "boundaries": boundaries}


class CS1ROAD_OT_import_spec(Operator, ImportHelper):
    bl_idname, bl_label, filename_ext = "cs1_road.import_spec", "Load road JSON", ".json"
    filter_glob: StringProperty(default="*.json", options={"HIDDEN"})

    def execute(self, context):
        data = json.loads(Path(self.filepath).read_text(encoding="utf-8"))
        props = context.scene.cs1_road_builder
        props.road_name = data.get("name", props.road_name)
        schema_version = int(data.get("schema_version", 2))
        cross = data.get("shared_geometry", data.get("cross_section", {}))
        props.curb_height = cross.get("curb_height", props.curb_height)
        props.surface_profile = cross.get("surface_profile", props.surface_profile)
        legacy_markings = data.get("markings", {})
        if schema_version >= 3:
            layout = data.get("layout", {})
            strips = {str(item.get("id", "")): item for item in layout.get("strips", [])}
            left_sidewalk = float(strips.get("strip-left-sidewalk", {}).get("width", props.sidewalk_width))
            right_sidewalk = float(strips.get("strip-right-sidewalk", {}).get("width", left_sidewalk))
            left_shoulder = float(strips.get("strip-left-shoulder", {}).get("width", 0.0))
            right_shoulder = float(strips.get("strip-right-shoulder", {}).get("width", left_shoulder))
            if abs(left_sidewalk - right_sidewalk) > 1e-6 or abs(left_shoulder - right_shoulder) > 1e-6:
                self.report({"ERROR"}, "Current Blender editor supports symmetric sidewalk and shoulder widths only")
                return {"CANCELLED"}
            props.sidewalk_width, props.shoulder_width = left_sidewalk, left_shoulder
            marking_styles = data.get("styles", {}).get("markings", {})
            solid = marking_styles.get("SOLID_WHITE", {})
            props.marking_paint_width = solid.get("paint_width", props.marking_paint_width)
            props.marking_region_width = solid.get("region_width", props.marking_region_width)
        else:
            props.shoulder_width = cross.get("shoulder_width", props.shoulder_width)
            props.sidewalk_width = cross.get("sidewalk_width", props.sidewalk_width)
            props.edge_lines = legacy_markings.get("edge_lines", props.edge_lines)
            props.lane_lines = legacy_markings.get("lane_lines", props.lane_lines)
            props.marking_paint_width = legacy_markings.get("paint_width", props.marking_paint_width)
            props.marking_region_width = legacy_markings.get("region_width", props.marking_region_width)
        node = data.get("node", {})
        props.node_transition_target = node.get("transition_target", node.get("far_profile", props.node_transition_target))
        props.node_shoulder_bands = node.get("shoulder_bands", props.node_shoulder_bands)
        props.lanes.clear()
        for source in data.get("lanes", []):
            lane_source = dict(source)
            if schema_version >= 3:
                strip = strips.get(str(source.get("surface_strip_id", "")), {})
                lane_source["width"] = float(source.get("lateral_end", 0.0)) - float(source.get("lateral_start", 0.0))
                if lane_source["width"] <= 0.0:
                    lane_source["width"] = strip.get("width", 3.0)
            _load_lane(props.lanes.add(), lane_source)
        _ensure_default_lanes(props)
        if schema_version >= 3:
            saved_boundaries = {str(item.get("id", "")): item for item in data.get("layout", {}).get("boundaries", [])}
            for item in props.boundaries:
                source = saved_boundaries.get(item.boundary_id)
                if source is None:
                    continue
                item.role = _enum_value(str(source.get("role", item.role)), BOUNDARY_ROLE_ITEMS, item.role)
                marking = source.get("marking")
                item.marking_enabled = marking is not None
                if marking is not None:
                    item.marking_role = _enum_value(str(marking.get("role", item.marking_role)), MARKING_ROLE_ITEMS, item.marking_role)
                    item.marking_style = _enum_value(str(marking.get("style_id", item.marking_style)), MARKING_STYLE_ITEMS, item.marking_style)
        else:
            _sync_boundaries(props, props.edge_lines, props.lane_lines)
        modes = data.get("modes", {})
        props.elevated_height = modes.get("elevated", {}).get("height", props.elevated_height)
        props.deck_depth = modes.get("elevated", {}).get("deck_depth", props.deck_depth)
        props.bridge_height = modes.get("bridge", {}).get("height", props.bridge_height)
        props.bridge_deck_depth = modes.get("bridge", {}).get("deck_depth", props.bridge_deck_depth)
        props.tunnel_depth = modes.get("tunnel", {}).get("depth", modes.get("slope", {}).get("tunnel_depth", props.tunnel_depth))
        props.tunnel_clearance = modes.get("tunnel", {}).get("clearance_height", props.tunnel_clearance)
        return {"FINISHED"}


class CS1ROAD_OT_export_spec(Operator, ExportHelper):
    bl_idname, bl_label, filename_ext = "cs1_road.export_spec", "Save road JSON", ".json"
    filter_glob: StringProperty(default="*.json", options={"HIDDEN"})

    def execute(self, context):
        props = context.scene.cs1_road_builder
        _ensure_lane_ids(props)
        _sync_boundaries(props)
        data = {
            "schema_version": 3, "name": props.road_name,
            "shared_geometry": {
                "segment_length": MODE_LENGTH, "node_length": MODE_LENGTH,
                "segment_slices": SEGMENT_SLICES, "node_slices": NODE_SLICES,
                "curb_height": props.curb_height, "surface_profile": props.surface_profile,
            },
            "styles": {"markings": {"SOLID_WHITE": {
                "paint_width": props.marking_paint_width,
                "region_width": props.marking_region_width,
                "texture_tile": "solid_white",
            }}},
            "layout": _export_layout(props),
            "node": {"length": MODE_LENGTH, "center_split": True, "shoulder_bands": props.node_shoulder_bands, "transition_target": props.node_transition_target},
            "lanes": _export_lanes(props),
            "modes": {
                "basic": {"shader": "Custom/Net/Road"},
                "elevated": {"height": props.elevated_height, "deck_depth": props.deck_depth, "shader": "Custom/Net/RoadBridge"},
                "bridge": {"height": props.bridge_height, "deck_depth": props.bridge_deck_depth, "shader": "Custom/Net/RoadBridge"},
                "slope": {"tunnel_depth": props.tunnel_depth, "shader": "Custom/Net/RoadBridge"},
                "tunnel": {"depth": props.tunnel_depth, "clearance_height": props.tunnel_clearance, "shader": "Custom/Net/RoadBridge"},
            },
        }
        Path(self.filepath).write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        return {"FINISHED"}


class CS1ROAD_PT_main(Panel):
    bl_label, bl_idname = "CS1 Road Builder", "CS1ROAD_PT_main"
    bl_space_type, bl_region_type, bl_category = "VIEW_3D", "UI", "Road"

    def draw(self, context):
        layout, props = self.layout, context.scene.cs1_road_builder
        layout.prop(props, "road_name")
        layout.prop(props, "mode")
        box = layout.box()
        box.label(text="Lanes (ordered left to right)")
        if not props.lanes:
            box.label(text="No lanes. Initialize the road definition.", icon="INFO")
            box.operator("cs1_road.lanes_reset", icon="FILE_REFRESH")
        row = box.row()
        row.template_list("CS1ROAD_UL_lanes", "", props, "lanes", props, "active_lane_index", rows=5)
        buttons = row.column(align=True)
        buttons.operator("cs1_road.lane_add", text="", icon="ADD")
        buttons.operator("cs1_road.lane_remove", text="", icon="REMOVE")
        buttons.operator("cs1_road.lane_move", text="", icon="TRIA_UP").direction = "UP"
        buttons.operator("cs1_road.lane_move", text="", icon="TRIA_DOWN").direction = "DOWN"
        if props.lanes:
            lane = props.lanes[min(props.active_lane_index, len(props.lanes) - 1)]
            details = box.column(align=True)
            details.label(text=f"ID: {lane.lane_id or '(assigned on build)'}")
            for field in ("zone", "width", "direction", "lane_type", "vehicle_type", "speed_limit", "vertical_offset", "stop_offset", "allow_connect"):
                details.prop(lane, field)
        counts = {key: sum(lane.direction == key for lane in props.lanes) for key in ("FORWARD", "BACKWARD", "BOTH")}
        box.label(text=f"Total {len(props.lanes)} / F {counts['FORWARD']} / B {counts['BACKWARD']} / Both {counts['BOTH']}")

        shared = layout.box()
        shared.label(text="Shared cross-section (all modes)")
        shared.prop(props, "shoulder_width")
        shared.prop(props, "sidewalk_width")
        shared.prop(props, "curb_height")
        roadway_width, total_width, _ = _cross_section(props)
        shared.label(text=f"Roadway {roadway_width:.3f} m / Total {total_width:.3f} m")
        shared.label(text="Segment length: fixed 64 m")
        shared.label(text="Curve mesh: 20 slices / 3.2 m")

        markings = layout.box()
        markings.label(text="Boundaries and segment markings")
        markings.template_list("CS1ROAD_UL_boundaries", "", props, "boundaries", props, "active_boundary_index", rows=6)
        markings.operator("cs1_road.boundaries_sync", icon="FILE_REFRESH")
        if props.boundaries:
            boundary = props.boundaries[min(props.active_boundary_index, len(props.boundaries) - 1)]
            markings.label(text=f"ID: {boundary.boundary_id}")
            markings.prop(boundary, "role")
            markings.prop(boundary, "marking_enabled")
            if boundary.marking_enabled:
                markings.prop(boundary, "marking_role")
                markings.prop(boundary, "marking_style")
        markings.label(text="Shared marking style")
        markings.prop(props, "marking_paint_width")
        markings.prop(props, "marking_region_width")
        if props.marking_region_width <= props.marking_paint_width:
            markings.label(text="Region must include asphalt margin.", icon="ERROR")
        markings.label(text="Node markings: none")

        node_box = layout.box()
        node_box.label(text="Ground node: fixed 64 m")
        node_box.label(text="Node mesh: 8 slices / 8 m")
        node_box.prop(props, "surface_profile")
        node_box.prop(props, "node_transition_target")
        node_box.prop(props, "node_shoulder_bands")
        node_box.label(text="Road surface split at X=0", icon="MOD_MIRROR")

        mode_box = layout.box()
        mode_box.label(text="Active mode only")
        if props.mode == "elevated":
            mode_box.prop(props, "elevated_height"); mode_box.prop(props, "deck_depth")
        elif props.mode == "bridge":
            mode_box.prop(props, "bridge_height"); mode_box.prop(props, "bridge_deck_depth")
        elif props.mode in {"slope", "tunnel"}:
            mode_box.prop(props, "tunnel_depth")
            if props.mode == "tunnel":
                mode_box.prop(props, "tunnel_clearance")

        layout.prop(props, "hide_other_modes")
        layout.operator("cs1_road.build_preview", icon="MOD_BUILD")
        layout.operator("cs1_road.build_all", icon="OUTLINER_COLLECTION")
        layout.operator("cs1_road.show_mode", icon="HIDE_OFF")
        row = layout.row(align=True)
        row.operator("cs1_road.import_spec", icon="IMPORT")
        row.operator("cs1_road.export_spec", icon="EXPORT")
        layout.label(text="Generated objects remain editable meshes.", icon="EDITMODE_HLT")


CLASSES = (
    CS1RoadLane, CS1RoadBoundary, CS1RoadBuilderProperties, CS1ROAD_UL_lanes, CS1ROAD_UL_boundaries,
    CS1ROAD_OT_lane_add, CS1ROAD_OT_lanes_reset, CS1ROAD_OT_lane_remove, CS1ROAD_OT_lane_move,
    CS1ROAD_OT_boundaries_sync,
    CS1ROAD_OT_build_preview, CS1ROAD_OT_build_all, CS1ROAD_OT_show_mode,
    CS1ROAD_OT_import_spec, CS1ROAD_OT_export_spec, CS1ROAD_PT_main,
)


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)
    bpy.types.Scene.cs1_road_builder = PointerProperty(type=CS1RoadBuilderProperties)
    if not bpy.app.timers.is_registered(_initialize_scene_lanes):
        bpy.app.timers.register(_initialize_scene_lanes, first_interval=0.0)


def unregister():
    if bpy.app.timers.is_registered(_initialize_scene_lanes):
        bpy.app.timers.unregister(_initialize_scene_lanes)
    del bpy.types.Scene.cs1_road_builder
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)
