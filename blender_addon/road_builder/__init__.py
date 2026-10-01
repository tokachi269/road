from __future__ import annotations

bl_info = {
    "name": "CS1 Road Builder",
    "author": "Local project",
    "version": (0, 8, 0),
    "blender": (5, 1, 0),
    "location": "3D View > Sidebar > Road",
    "description": "Manage, edit, and preview Cities: Skylines 1 road definitions",
    "category": "Object",
}

import json
import importlib
import time
from dataclasses import dataclass
from pathlib import Path

import bmesh
import bpy
from bpy.props import BoolProperty, CollectionProperty, EnumProperty, FloatProperty, FloatVectorProperty, IntProperty, PointerProperty, StringProperty
from bpy.types import Operator, Panel, PropertyGroup, UIList
from bpy_extras.io_utils import ExportHelper, ImportHelper

# Blender's Reload Scripts reloads this package, but Python otherwise keeps
# package children cached. Reload the Blender-independent modules explicitly
# even on the first reload from an older add-on version.
from . import domain as _domain
from . import geometry_plan as _geometry_plan
from . import runtime_export as _runtime_export

importlib.reload(_domain)
importlib.reload(_geometry_plan)
importlib.reload(_runtime_export)

from .domain import (
    MODE_LENGTH,
    MEDIAN_END_OVERHANG,
    MEDIAN_Z_FIGHT_EPSILON,
    NODE_SLICES,
    PARKING_LANE_DEFAULT_WIDTH,
    ROADWAY_DEPRESSION,
    SIDEWALK_LANE_TOTAL_INSET,
    SEGMENT_SLICES,
    allocate_cross_section,
    cross_section_widths,
    expected_boundaries,
    lane_vertical_offset,
    marking_rule,
    median_split_index,
    parking_fits,
    roadway_depression,
    sidewalk_lane_width,
    strip_id,
)
from .geometry_plan import plan_main_girders
from .runtime_export import (
    export_prop_bundle,
    export_runtime_bundle,
    geometry_fingerprint,
    safe_road_id,
    texture_fingerprint,
)


MODE_ITEMS = (
    ("basic", "Ground", "Ground road"),
    ("elevated", "Elevated", "Elevated road"),
    ("bridge", "Bridge", "Bridge road"),
    ("slope", "Entrance", "Transition between ground and tunnel"),
    ("tunnel", "Tunnel", "Underground road"),
)
MODE_PREVIEW_SPACING = 16.0
LANE_ZONE_ITEMS = (
    ("ROAD", "Roadway", "Lane occupies the roadway cross-section"),
    ("LEFT_SIDEWALK", "Pedestrian L", "Network lane lies on the left sidewalk"),
    ("RIGHT_SIDEWALK", "Pedestrian R", "Network lane lies on the right sidewalk"),
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
MEDIAN_PROFILE_ITEMS = (
    ("NONE", "None", "No median"),
    ("MESH", "Small / mesh", "Fit the selected mesh to the median dimensions"),
    ("CURB", "Large / generated curb", "Generate a curb-surrounded median"),
)
ROADSIDE_USE_ITEMS = (
    ("SHOULDER", "Shoulder", "Keep the remaining width as non-lane roadside space"),
    ("PARKING", "Parking", "Create symmetric parking lanes when both sides fit"),
)
AUTO_PARKING_LANE_IDS = {"lane-auto-parking-left", "lane-auto-parking-right"}
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
ROAD_VEHICLE_TYPE_ITEMS = tuple(item for item in VEHICLE_TYPE_ITEMS if item[0] != "NONE")
SIDEWALK_VEHICLE_TYPE_ITEMS = (("NONE", "None", "Pedestrian lanes do not carry vehicles"),)


def _vehicle_type_items_for_lane(lane, context):
    if lane.zone in {"LEFT_SIDEWALK", "RIGHT_SIDEWALK"}:
        return SIDEWALK_VEHICLE_TYPE_ITEMS
    if lane.lane_type == "VEHICLE":
        return ROAD_VEHICLE_TYPE_ITEMS
    return VEHICLE_TYPE_ITEMS


def _on_lane_zone_update(lane, context) -> None:
    if lane.zone in {"LEFT_SIDEWALK", "RIGHT_SIDEWALK"}:
        lane.direction = "BOTH"
        lane.lane_type = "PEDESTRIAN"
        lane.vehicle_type = "NONE"
    elif lane.lane_type == "PEDESTRIAN":
        lane.lane_type = "VEHICLE"
        lane.vehicle_type = "CAR"
    scene = getattr(context, "scene", None)
    props = active_road(scene) if scene is not None else None
    if props is not None:
        _sync_lane_derived_values(props)
        _sync_cross_section_state(props)
        _schedule_live_preview(props, context)
BOUNDARY_ROLE_ITEMS = (
    ("CURB", "Curb", "Boundary between sidewalk and the road body"),
    ("CARRIAGEWAY_EDGE", "Carriageway edge", "Boundary between carriageway and shoulder"),
    ("LANE_DIVIDER", "Lane divider", "Boundary between adjacent carriageway strips"),
    ("MEDIAN_EDGE", "Median edge", "Boundary between roadway and median"),
)
MARKING_ROLE_ITEMS = (
    ("CARRIAGEWAY_EDGE", "Roadside line", "Carriageway edge marking"),
    ("CENTER_LINE", "Center line", "Boundary between opposing traffic"),
    ("LANE_SEPARATOR", "Lane separator", "Boundary between lanes in the same direction"),
)
MARKING_STYLE_ITEMS = (
    ("SOLID_WHITE", "Solid white", "Shared solid white marking style"),
    ("DASHED_WHITE", "Dashed white", "Shared dashed white marking style"),
    ("SOLID_YELLOW", "Solid yellow", "Solid yellow centre-line style"),
)
LANE_SEPARATOR_STYLE_ITEMS = tuple(
    item for item in MARKING_STYLE_ITEMS if item[0] in {"SOLID_WHITE", "DASHED_WHITE"}
)
CENTER_LINE_STYLE_ITEMS = MARKING_STYLE_ITEMS
IMT_APPEARANCE_PRESET_ITEMS = (
    ("JP_WEATHERED", "JP weathered", "Shared worn-paint baseline used by this project"),
    ("CUSTOM", "Custom", "Keep the editable appearance values below"),
)
MARKING_TEXTURE_REGIONS = {
    "SOLID_WHITE": "line.solid.white",
    "DASHED_WHITE": "line.dashed.white",
    # The surface atlas supplies the paint mask. IMT applies the configured
    # yellow colour at runtime for this semantic style.
    "SOLID_YELLOW": "line.solid.white",
}
SHARED_SURFACE_MATERIAL = "CS1 Road Shared Surface"
SHARED_STRUCTURE_MATERIAL = "CS1 Road Shared Structure"
SHARED_TUNNEL_MATERIAL = "CS1 Road Shared Tunnel"
DEFAULT_ROAD_COLOR = (0.12, 0.14, 0.16)
FACE_KIND_VALUES = {"surface": 0, "marking": 1, "structure": 2}
UV_REGION_NAMES = (
    "authored",
    "lane.default", "shoulder.default", "sidewalk.default",
    "curb.upper", "curb.wall", "curb.lower",
    "sidewalk.default+curb.upper",
    "curb.lower+shoulder.default", "curb.lower+lane.default",
    "line.solid.white", "line.dashed.white",
    "deck.underside", "elevated.fascia", "bridge.fascia",
    "girder.bottom", "girder.side",
    "tunnel.roof", "tunnel.wall",
)
UV_REGION_VALUES = {name: index for index, name in enumerate(UV_REGION_NAMES)}
_AUTO_EXPORT_STATE = {}
_AUTO_EXPORT_ERRORS = {}
_LIVE_PREVIEW_PENDING = {}
_LIVE_PREVIEW_DELAY = 0.15
_LIVE_PREVIEW_SETTLE_DELAY = 0.60
_LIVE_PREVIEW_REBUILDING = False
_IMT_PRESET_APPLYING = False
_CROSS_SECTION_SYNCING = False
DEFAULT_RUNTIME_OUTPUT = str(Path(__file__).resolve().parents[2] / "build" / "runtime-preview")
DEFAULT_TEXTURE_LAYOUT = (
    Path(__file__).resolve().parents[2]
    / "textures"
    / "dimensions.json"
)
_TEXTURE_LAYOUT_CACHE = None
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


def _texture_layout_manifest() -> dict:
    global _TEXTURE_LAYOUT_CACHE
    mtime_ns = DEFAULT_TEXTURE_LAYOUT.stat().st_mtime_ns
    if (
        _TEXTURE_LAYOUT_CACHE is None
        or _TEXTURE_LAYOUT_CACHE[0] != mtime_ns
    ):
        _TEXTURE_LAYOUT_CACHE = (
            mtime_ns,
            json.loads(DEFAULT_TEXTURE_LAYOUT.read_text(encoding="utf-8")),
        )
    return _TEXTURE_LAYOUT_CACHE[1]


def _generated_atlas_path(family: str, map_id: str = "d") -> Path:
    generator = _texture_layout_manifest()["photoshop_generator"]
    return (
        DEFAULT_TEXTURE_LAYOUT.parent
        / generator["assets_directory"]
        / generator["families"][family][map_id]
    )


def _atlas_texture_region(
    atlas_name: str, region_id: str,
) -> tuple[float, float, float]:
    manifest = _texture_layout_manifest()
    atlas_width = float(manifest["atlas_width_px"])
    layers = {item["file"]: item for item in manifest["layers"]}
    for slot in manifest["atlas_layout"][atlas_name]["slots"]:
        for region in slot["regions"]:
            if region["id"] == region_id:
                start = float(region["x_px"])
                end = start + float(region["width_px"])
                return start / atlas_width, end / atlas_width, float(
                    layers[region["file"]]["meters"]
                )
    raise ValueError(f"Unknown {atlas_name} texture region: {region_id}")


def _texture_region(region_id: str) -> tuple[float, float, float]:
    return _atlas_texture_region("surface", region_id)


def _uv_region_bounds(region_id: str) -> tuple[str, float, float]:
    manifest = _texture_layout_manifest()
    profile = manifest.get("uv_profiles", {}).get(region_id)
    if profile is not None:
        atlas_width = float(manifest["atlas_width_px"])
        u_min = float(profile["x_px"]) / atlas_width
        u_max = (float(profile["x_px"]) + float(profile["width_px"])) / atlas_width
        return profile["atlas"], u_min, u_max
    for atlas_name in ("surface", "structure", "tunnel"):
        try:
            u_min, u_max, _ = _atlas_texture_region(atlas_name, region_id)
        except ValueError:
            continue
        return atlas_name, u_min, u_max
    raise ValueError(f"Unknown UV region: {region_id}")


def _generator_image(
    family: str, map_id: str = "d", force_reload: bool = False,
) -> bpy.types.Image | None:
    image_path = _generated_atlas_path(family, map_id)
    map_contract = _texture_layout_manifest()["photoshop_generator"]["maps"][map_id]
    if not image_path.is_file():
        if map_contract.get("required"):
            raise FileNotFoundError(
                f"Photoshop Generator output does not exist: {image_path}"
            )
        return None
    image = bpy.data.images.load(str(image_path), check_existing=True)
    if map_id != "d":
        image.colorspace_settings.name = "Non-Color"
    mtime_ns = image_path.stat().st_mtime_ns
    mtime_key = str(mtime_ns)
    if force_reload or image.get("cs1_source_mtime_ns") != mtime_key:
        image.reload()
        image["cs1_source_mtime_ns"] = mtime_key
    return image


def _surface_atlas_image(force_reload: bool = False) -> bpy.types.Image:
    return _generator_image("surface", "d", force_reload)


def _add_generator_preview_maps(
    material, nodes, links, mapping, shader, family, force_reload,
) -> None:
    labels = {
        "a": "CS1 Alpha",
        "p": "CS1 Pavement mask (runtime theme)",
        "r": "CS1 Road mask (runtime theme)",
        "n": "CS1 Normal",
        "s": "CS1 Specular",
    }
    texture_y = {
        "a": 160.0,
        "p": -80.0,
        "r": -320.0,
        "n": -560.0,
        "s": -800.0,
    }
    for map_id in ("a", "p", "r", "n", "s"):
        image = _generator_image(family, map_id, force_reload)
        if image is None:
            continue
        texture = nodes.new("ShaderNodeTexImage")
        texture.name = labels[map_id]
        texture.label = labels[map_id]
        texture.location = (-520.0, texture_y[map_id])
        texture.width = 240.0
        texture.image = image
        texture.extension = "REPEAT"
        links.new(mapping.outputs["Vector"], texture.inputs["Vector"])
        if map_id == "a" and shader.inputs.get("Alpha") is not None:
            links.new(texture.outputs["Color"], shader.inputs["Alpha"])
            if hasattr(material, "surface_render_method"):
                material.surface_render_method = "DITHERED"
        elif map_id == "n" and shader.inputs.get("Normal") is not None:
            normal = nodes.new("ShaderNodeNormalMap")
            normal.name = "CS1 Normal conversion"
            normal.location = (-180.0, texture_y[map_id])
            links.new(texture.outputs["Color"], normal.inputs["Color"])
            links.new(normal.outputs["Normal"], shader.inputs["Normal"])
        elif map_id == "s":
            specular = shader.inputs.get("Specular IOR Level")
            if specular is None:
                specular = shader.inputs.get("Specular")
            if specular is not None:
                links.new(texture.outputs["Color"], specular)


def _image_texture_material(
    name: str, family: str, fallback_color, force_reload: bool = False,
) -> bpy.types.Material:
    image = _generator_image(family, "d", force_reload)
    material = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    material.diffuse_color = fallback_color
    material.use_nodes = True
    nodes = material.node_tree.nodes
    links = material.node_tree.links
    nodes.clear()
    output = nodes.new("ShaderNodeOutputMaterial")
    output.location = (500.0, 100.0)
    shader = nodes.new("ShaderNodeBsdfPrincipled")
    shader.location = (160.0, 100.0)
    uv_map = nodes.new("ShaderNodeUVMap")
    uv_map.location = (-1040.0, 100.0)
    uv_map.uv_map = "RoadUV"
    mapping = nodes.new("ShaderNodeMapping")
    mapping.location = (-800.0, 100.0)
    mapping.inputs["Scale"].default_value = (1.0, 2.0, 1.0)
    texture = nodes.new("ShaderNodeTexImage")
    texture.name = "CS1 Diffuse"
    texture.label = "CS1 Diffuse"
    texture.location = (-520.0, 420.0)
    texture.width = 240.0
    texture.image = image
    texture.extension = "REPEAT"
    links.new(uv_map.outputs["UV"], mapping.inputs["Vector"])
    links.new(mapping.outputs["Vector"], texture.inputs["Vector"])
    links.new(texture.outputs["Color"], shader.inputs["Base Color"])
    _add_generator_preview_maps(
        material, nodes, links, mapping, shader, family, force_reload,
    )
    links.new(shader.outputs[0], output.inputs[0])
    return material


def _material(
    paint_width: float, region_width: float, road_color,
    force_texture_reload: bool = False,
) -> bpy.types.Material:
    material = (
        bpy.data.materials.get(SHARED_SURFACE_MATERIAL)
        or bpy.data.materials.new(SHARED_SURFACE_MATERIAL)
    )
    material.diffuse_color = (*tuple(road_color), 1.0)
    material.use_nodes = True
    nodes = material.node_tree.nodes
    links = material.node_tree.links
    nodes.clear()
    output = nodes.new("ShaderNodeOutputMaterial")
    output.location = (500.0, 100.0)
    shader = nodes.new("ShaderNodeBsdfPrincipled")
    shader.location = (160.0, 100.0)
    uv_map = nodes.new("ShaderNodeUVMap")
    uv_map.location = (-1040.0, 100.0)
    uv_map.uv_map = "RoadUV"
    mapping = nodes.new("ShaderNodeMapping")
    mapping.location = (-800.0, 100.0)
    mapping.inputs["Scale"].default_value = (1.0, 2.0, 1.0)
    texture = nodes.new("ShaderNodeTexImage")
    texture.name = "CS1 Diffuse"
    texture.label = "CS1 Diffuse"
    texture.location = (-520.0, 420.0)
    texture.width = 240.0
    texture.image = _surface_atlas_image(force_texture_reload)
    texture.extension = "REPEAT"
    links.new(uv_map.outputs["UV"], mapping.inputs["Vector"])
    links.new(mapping.outputs["Vector"], texture.inputs["Vector"])
    links.new(texture.outputs["Color"], shader.inputs["Base Color"])
    _add_generator_preview_maps(
        material, nodes, links, mapping, shader, "surface", force_texture_reload,
    )
    links.new(shader.outputs[0], output.inputs[0])
    return material


def _structure_material(force_reload: bool = False) -> bpy.types.Material:
    return _image_texture_material(
        SHARED_STRUCTURE_MATERIAL,
        "structure",
        (0.34, 0.36, 0.38, 1.0),
        force_reload,
    )


def _tunnel_material(force_reload: bool = False) -> bpy.types.Material:
    return _image_texture_material(
        SHARED_TUNNEL_MATERIAL,
        "tunnel",
        (0.20, 0.22, 0.24, 1.0),
        force_reload,
    )


def _reload_loaded_generator_images() -> int:
    reloaded = 0
    generator = _texture_layout_manifest()["photoshop_generator"]
    expected = {
        _generated_atlas_path(family, map_id).resolve(): bool(
            generator["maps"][map_id].get("required")
        )
        for family in ("surface", "structure", "tunnel")
        for map_id in generator["maps"]
    }
    for image in bpy.data.images:
        if image.source != "FILE" or not image.filepath:
            continue
        try:
            image_path = Path(bpy.path.abspath(image.filepath)).resolve()
        except (OSError, ValueError):
            continue
        if image_path not in expected:
            continue
        if not image_path.is_file():
            if expected[image_path]:
                raise FileNotFoundError(
                    f"Photoshop Generator output does not exist: {image_path}"
                )
            continue
        mtime_key = str(image_path.stat().st_mtime_ns)
        if image.get("cs1_source_mtime_ns") != mtime_key:
            image.reload()
            image["cs1_source_mtime_ns"] = mtime_key
            reloaded += 1
    return reloaded


def _mesh_object(
    collection, name, vertices, faces, face_kinds, face_uvs, face_uv_regions,
    materials,
    material_indices=None,
) -> bpy.types.Object:
    mesh = bpy.data.meshes.new(f"{name}_mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    uv_layer = mesh.uv_layers.new(name="RoadUV")
    kind_attribute = mesh.attributes.new(
        name="cs1_face_kind", type="INT", domain="FACE",
    )
    region_attribute = mesh.attributes.new(
        name="cs1_uv_region", type="INT", domain="FACE",
    )
    for polygon, kind, explicit_uvs, uv_region in zip(
        mesh.polygons, face_kinds, face_uvs, face_uv_regions,
    ):
        polygon.material_index = (material_indices or {}).get(kind, 0)
        kind_attribute.data[polygon.index].value = FACE_KIND_VALUES.get(kind, 0)
        if explicit_uvs is None:
            raise ValueError(f"{name}: face {polygon.index} has no explicit UV")
        if len(explicit_uvs) != len(polygon.loop_indices):
            raise ValueError(f"{name}: face {polygon.index} UV loop count disagrees")
        try:
            region_attribute.data[polygon.index].value = UV_REGION_VALUES[uv_region]
        except KeyError as error:
            raise ValueError(
                f"{name}: face {polygon.index} has unknown UV region {uv_region!r}"
            ) from error
        for loop_index, uv in zip(polygon.loop_indices, explicit_uvs):
            uv_layer.data[loop_index].uv = uv
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
        self.face_uv_regions = []
        self._vertex_map = {}

    def _vertex(self, coordinate, seam="") -> int:
        key = (seam, tuple(round(float(value), 6) for value in coordinate))
        if key not in self._vertex_map:
            self._vertex_map[key] = len(self.vertices)
            self.vertices.append(tuple(coordinate))
        return self._vertex_map[key]

    def polygon(
        self, points, seams=None, kind="surface", uvs=None,
        uv_region=None,
    ) -> None:
        if len(points) < 3:
            return
        if seams is None:
            seams = ("",) * len(points)
        self.faces.append(tuple(self._vertex(point, seam) for point, seam in zip(points, seams)))
        self.face_kinds.append(kind)
        self.face_uvs.append(tuple(uvs) if uvs is not None else None)
        self.face_uv_regions.append(uv_region)

    def quad(
        self, a, b, c, d, seams=("", "", "", ""), kind="surface",
        uvs=None, uv_region=None,
    ) -> None:
        self.polygon((a, b, c, d), seams, kind, uvs, uv_region)

    def horizontal_strip(
        self, x_min, x_max, y_min, y_max, start_z, end_z,
        slices=1, flip=False, kind="surface", u_range=None, uv_region=None,
    ) -> None:
        for index in range(slices):
            t0, t1 = index / slices, (index + 1) / slices
            ya, yb = y_min + (y_max - y_min) * t0, y_min + (y_max - y_min) * t1
            za, zb = start_z + (end_z - start_z) * t0, start_z + (end_z - start_z) * t1
            points = ((x_min, ya, za), (x_max, ya, za), (x_max, yb, zb), (x_min, yb, zb))
            uvs = None
            if u_range is not None:
                u_min, u_max = u_range
                uvs = ((u_min, t0), (u_max, t0), (u_max, t1), (u_min, t1))
            if flip:
                points = tuple(reversed(points))
                uvs = tuple(reversed(uvs)) if uvs is not None else None
            self.quad(*points, kind=kind, uvs=uvs, uv_region=uv_region)

    def vertical_strip(
        self, x, y_min, y_max, start_bottom, end_bottom, start_top, end_top,
        flip=False, slices=1, kind="surface", u_range=None, uv_region=None,
    ) -> None:
        for index in range(slices):
            t0, t1 = index / slices, (index + 1) / slices
            ya, yb = y_min + (y_max - y_min) * t0, y_min + (y_max - y_min) * t1
            ba, bb = start_bottom + (end_bottom - start_bottom) * t0, start_bottom + (end_bottom - start_bottom) * t1
            ta, tb = start_top + (end_top - start_top) * t0, start_top + (end_top - start_top) * t1
            points = ((x, ya, ba), (x, yb, bb), (x, yb, tb), (x, ya, ta))
            uvs = None
            if u_range is not None:
                u_min, u_max = u_range
                uvs = ((u_min, t0), (u_min, t1), (u_max, t1), (u_max, t0))
            if flip:
                points = tuple(reversed(points))
                uvs = tuple(reversed(uvs)) if uvs is not None else None
            self.quad(*points, kind=kind, uvs=uvs, uv_region=uv_region)

    def create(self, collection, name, materials, material_indices=None) -> bpy.types.Object:
        return _mesh_object(
            collection, name, self.vertices, self.faces, self.face_kinds,
            self.face_uvs, self.face_uv_regions, materials, material_indices,
        )


@dataclass(frozen=True)
class _ElevatedEdgePlan:
    polygons: tuple
    connector_x: float
    source_name: str
    mirrored: bool


@dataclass(frozen=True)
class _MedianMeshPlan:
    polygons: tuple
    source_name: str


def _mesh_object_poll(_self, obj) -> bool:
    return obj is None or obj.type == "MESH"


def _group_vertex_indices(source, group_name: str) -> set[int]:
    if not group_name.strip():
        return set()
    group = source.vertex_groups.get(group_name)
    if group is None:
        return set()
    return {
        vertex.index
        for vertex in source.data.vertices
        if any(item.group == group.index and item.weight > 0.0 for item in vertex.groups)
    }


def _prepare_elevated_edge_mesh(
    source, side: str, anchor_x: float, surface_z: float, deck_depth: float,
    slices: int, group_name: str,
) -> _ElevatedEdgePlan:
    if source is None or source.type != "MESH":
        raise ValueError(f"Elevated {side} edge must be a Mesh Object")

    no_split_vertices = _group_vertex_indices(source, group_name)
    selected_faces = {
        polygon.index for polygon in source.data.polygons
        if not any(index in no_split_vertices for index in polygon.vertices)
    }
    if not selected_faces:
        raise ValueError(f"{source.name}: no faces remain for longitudinal slicing")
    selected_vertices = {
        index
        for polygon in source.data.polygons if polygon.index in selected_faces
        for index in polygon.vertices
    }

    basis = source.matrix_world.to_3x3()
    mirrored = side == "left"
    coordinates = []
    for vertex in source.data.vertices:
        coordinate = basis @ vertex.co
        if abs(coordinate.x) <= 1e-4:
            coordinate.x = 0.0
        if abs(coordinate.z) <= 1e-4:
            coordinate.z = 0.0
        if mirrored:
            coordinate.x = -coordinate.x
        coordinates.append(coordinate)
    curve_coordinates = [coordinates[index] for index in selected_vertices]
    y_min = min(value.y for value in curve_coordinates)
    y_max = max(value.y for value in curve_coordinates)
    half_length = MODE_LENGTH * 0.5
    if abs(y_min + half_length) > 1e-4 or abs(y_max - half_length) > 1e-4:
        raise ValueError(
            f"{source.name}: sliced faces must span local Y "
            f"{-half_length:g}..{half_length:g} m (found {y_min:g}..{y_max:g})"
        )

    reference_bottom = min(value.z for value in curve_coordinates)
    if reference_bottom >= -1e-6:
        raise ValueError(
            f"{source.name}: sliced faces need geometry below local Z=0 "
            "for deck-depth scaling"
        )
    bottom_indices = {
        index for index in selected_vertices
        if abs(coordinates[index].z - reference_bottom) <= 1e-4
    }
    bottom_x_values = [coordinates[index].x for index in bottom_indices]
    # A solid side piece has two longitudinal edges at its lowest face.  The
    # edge nearest the road centre is the deck connector; the outer edge stays
    # part of the supplied cross-section.
    connector_local_x = (
        max(bottom_x_values) if side == "left" else min(bottom_x_values)
    )
    connector_indices = {
        index for index in bottom_indices
        if abs(coordinates[index].x - connector_local_x) <= 1e-4
    }
    bottom_y_values = [coordinates[index].y for index in connector_indices]
    if (
        abs(min(bottom_y_values) + half_length) > 1e-4
        or abs(max(bottom_y_values) - half_length) > 1e-4
    ):
        raise ValueError(
            f"{source.name}: road-centre-side lowest edge must span "
            "the full 64 m length"
        )

    connector_x = anchor_x + connector_local_x

    working = source.data.copy()
    depth_scale = deck_depth / -reference_bottom
    for vertex, coordinate in zip(working.vertices, coordinates):
        z = coordinate.z * depth_scale if coordinate.z < 0.0 else coordinate.z
        if vertex.index in bottom_indices:
            z = -deck_depth
        vertex.co = (coordinate.x + anchor_x, coordinate.y, z + surface_z)

    bm = bmesh.new()
    result_mesh = None
    try:
        bm.from_mesh(working)
        bm.faces.ensure_lookup_table()
        split_layer = bm.faces.layers.int.new("cs1_curve_split")
        for face in bm.faces:
            face[split_layer] = 1 if face.index in selected_faces else 0

        # Only the road-centre-side face can be buried inside the generated
        # deck.  Split that interface at the surface and discard its lower
        # portion.  A global Z split would add a needless horizontal loop to
        # every exposed face of a solid custom edge.
        connector_faces = [
            face for face in bm.faces
            if face.is_valid
            and face[split_layer] == 1
            and all(abs(vertex.co.x - connector_x) <= 1e-4 for vertex in face.verts)
            and min(vertex.co.z for vertex in face.verts) < surface_z - 1e-6
            and max(vertex.co.z for vertex in face.verts) > surface_z + 1e-6
        ]
        if connector_faces:
            connector_edges = {edge for face in connector_faces for edge in face.edges}
            connector_vertices = {vertex for face in connector_faces for vertex in face.verts}
            bmesh.ops.bisect_plane(
                bm,
                geom=[*connector_vertices, *connector_edges, *connector_faces],
                plane_co=(0.0, 0.0, surface_z),
                plane_no=(0.0, 0.0, 1.0),
                dist=1e-6,
                clear_inner=False,
                clear_outer=False,
            )
            buried_faces = [
                face for face in bm.faces
                if face.is_valid
                and all(abs(vertex.co.x - connector_x) <= 1e-4 for vertex in face.verts)
                and max(vertex.co.z for vertex in face.verts) <= surface_z + 1e-6
                and min(vertex.co.z for vertex in face.verts) < surface_z - 1e-6
            ]
            if buried_faces:
                bmesh.ops.delete(bm, geom=buried_faces, context="FACES")

        for index in range(1, slices):
            plane_y = -half_length + MODE_LENGTH * index / slices
            split_faces = [face for face in bm.faces if face.is_valid and face[split_layer] == 1]
            split_edges = {edge for face in split_faces for edge in face.edges}
            split_vertices = {vertex for face in split_faces for vertex in face.verts}
            bmesh.ops.bisect_plane(
                bm,
                geom=[*split_vertices, *split_edges, *split_faces],
                plane_co=(0.0, plane_y, 0.0),
                plane_no=(0.0, 1.0, 0.0),
                dist=1e-6,
                clear_inner=False,
                clear_outer=False,
            )

        result_mesh = bpy.data.meshes.new(f"{source.name}_road_edge_work")
        bm.to_mesh(result_mesh)
        result_mesh.update()
        uv_layer = result_mesh.uv_layers.active
        polygons = []
        for polygon in result_mesh.polygons:
            points = tuple(tuple(result_mesh.vertices[index].co) for index in polygon.vertices)
            # Segment/node ends meet another network mesh.  Source end caps
            # are hidden there and only add overlapping faces.
            if (
                max(point[1] for point in points) - min(point[1] for point in points) <= 1e-6
                and abs(abs(points[0][1]) - half_length) <= 1e-4
            ):
                continue
            uvs = None
            if uv_layer is not None:
                uvs = tuple(tuple(uv_layer.data[index].uv) for index in polygon.loop_indices)
            if mirrored:
                points = tuple(reversed(points))
                if uvs is not None:
                    uvs = tuple(reversed(uvs))
            polygons.append((points, uvs))
        return _ElevatedEdgePlan(tuple(polygons), connector_x, source.name, mirrored)
    finally:
        bm.free()
        bpy.data.meshes.remove(working)
        if result_mesh is not None:
            bpy.data.meshes.remove(result_mesh)


def _append_elevated_edge(mesh, plan: _ElevatedEdgePlan) -> None:
    u_range = _atlas_texture_region("structure", "elevated.fascia")[:2]
    for points, uvs in plan.polygons:
        uv_region = "authored"
        if uvs is None:
            uvs = _polygon_region_uvs(points, u_range)
            uv_region = "elevated.fascia"
        mesh.polygon(
            points, kind="structure", uvs=uvs, uv_region=uv_region,
        )


def _prepare_median_mesh(
    source, width: float, height: float, road_start: float, road_end: float,
    slices: int, group_name: str,
) -> _MedianMeshPlan:
    if source is None or source.type != "MESH":
        raise ValueError("Median custom mesh must be a Mesh Object")

    no_split_vertices = _group_vertex_indices(source, group_name)
    selected_faces = {
        polygon.index for polygon in source.data.polygons
        if not any(index in no_split_vertices for index in polygon.vertices)
    }
    if not selected_faces:
        raise ValueError(f"{source.name}: no median faces remain for longitudinal slicing")

    basis = source.matrix_world.to_3x3()
    coordinates = [basis @ vertex.co for vertex in source.data.vertices]
    x_min = min(value.x for value in coordinates)
    x_max = max(value.x for value in coordinates)
    y_min = min(value.y for value in coordinates)
    y_max = max(value.y for value in coordinates)
    z_min = min(value.z for value in coordinates)
    z_max = max(value.z for value in coordinates)
    if x_max - x_min <= 1e-6 or z_max - z_min <= 1e-6:
        raise ValueError(f"{source.name}: median mesh needs non-zero local width and height")
    half_length = MODE_LENGTH * 0.5
    if abs(y_min + half_length) > 1e-4 or abs(y_max - half_length) > 1e-4:
        raise ValueError(
            f"{source.name}: median mesh must span local Y "
            f"{-half_length:g}..{half_length:g} m (found {y_min:g}..{y_max:g})"
        )

    working = source.data.copy()
    target_y_min = -half_length - MEDIAN_END_OVERHANG
    target_y_max = half_length + MEDIAN_END_OVERHANG
    for vertex, coordinate in zip(working.vertices, coordinates):
        x_t = (coordinate.x - x_min) / (x_max - x_min)
        y_t = (coordinate.y - y_min) / (y_max - y_min)
        z_t = (coordinate.z - z_min) / (z_max - z_min)
        x = -width * 0.5 + width * x_t
        y = target_y_min + (target_y_max - target_y_min) * y_t
        road_t = min(1.0, max(0.0, (y + half_length) / MODE_LENGTH))
        road_z = road_start + (road_end - road_start) * road_t
        z = road_z - MEDIAN_Z_FIGHT_EPSILON + (
            height + MEDIAN_Z_FIGHT_EPSILON
        ) * z_t
        vertex.co = (x, y, z)

    bm = bmesh.new()
    result_mesh = None
    try:
        bm.from_mesh(working)
        bm.faces.ensure_lookup_table()
        split_layer = bm.faces.layers.int.new("cs1_curve_split")
        for face in bm.faces:
            face[split_layer] = 1 if face.index in selected_faces else 0
        for index in range(1, slices):
            plane_y = -half_length + MODE_LENGTH * index / slices
            split_faces = [
                face for face in bm.faces
                if face.is_valid and face[split_layer] == 1
            ]
            split_edges = {edge for face in split_faces for edge in face.edges}
            split_vertices = {vertex for face in split_faces for vertex in face.verts}
            bmesh.ops.bisect_plane(
                bm,
                geom=[*split_vertices, *split_edges, *split_faces],
                plane_co=(0.0, plane_y, 0.0),
                plane_no=(0.0, 1.0, 0.0),
                dist=1e-6,
                clear_inner=False,
                clear_outer=False,
            )

        result_mesh = bpy.data.meshes.new(f"{source.name}_median_work")
        bm.to_mesh(result_mesh)
        result_mesh.update()
        uv_layer = result_mesh.uv_layers.active
        polygons = []
        for polygon in result_mesh.polygons:
            points = tuple(
                tuple(result_mesh.vertices[index].co) for index in polygon.vertices
            )
            if (
                max(point[1] for point in points) - min(point[1] for point in points)
                <= 1e-6
                and abs(abs(points[0][1]) - (half_length + MEDIAN_END_OVERHANG))
                <= 1e-4
            ):
                continue
            uvs = None
            if uv_layer is not None:
                uvs = tuple(
                    tuple(uv_layer.data[index].uv) for index in polygon.loop_indices
                )
            polygons.append((points, uvs))
        return _MedianMeshPlan(tuple(polygons), source.name)
    finally:
        bm.free()
        bpy.data.meshes.remove(working)
        if result_mesh is not None:
            bpy.data.meshes.remove(result_mesh)


def _append_median_mesh(mesh, plan: _MedianMeshPlan) -> None:
    u_range = _texture_region("sidewalk.default")[:2]
    for points, uvs in plan.polygons:
        uv_region = "authored"
        if uvs is None:
            uvs = _polygon_region_uvs(points, u_range)
            uv_region = "sidewalk.default"
        mesh.polygon(points, kind="surface", uvs=uvs, uv_region=uv_region)


def _polygon_region_uvs(points, u_range):
    u_min, u_max = u_range
    x_values = [point[0] for point in points]
    z_values = [point[2] for point in points]
    use_z = max(z_values) - min(z_values) >= max(x_values) - min(x_values)
    cross_values = z_values if use_z else x_values
    cross_min, cross_max = min(cross_values), max(cross_values)
    cross_size = max(cross_max - cross_min, 1e-8)
    result = []
    for point, cross_value in zip(points, cross_values):
        ratio = (cross_value - cross_min) / cross_size
        u = u_min + (u_max - u_min) * ratio
        v = min(1.0, max(0.0, (point[1] + MODE_LENGTH * 0.5) / MODE_LENGTH))
        result.append((u, v))
    return tuple(result)


def _add_generated_median(
    mesh, median_range, y_min, y_max, road_start, road_end, height,
    curb_width, slices,
) -> None:
    wall_u_min, wall_u_max, _ = _texture_region("curb.wall")
    upper_u_min, upper_u_max, _ = _texture_region("curb.upper")
    sidewalk_u_min, sidewalk_u_max, _ = _texture_region("sidewalk.default")
    left, right = median_range
    width = right - left
    if curb_width * 2.0 >= width - 1e-8:
        raise ValueError("Median curb top width must be less than half the median width")
    inner_left = left + curb_width
    inner_right = right - curb_width
    y_min -= MEDIAN_END_OVERHANG
    y_max += MEDIAN_END_OVERHANG
    length = y_max - y_min
    for index in range(slices):
        t0, t1 = index / slices, (index + 1) / slices
        ya, yb = y_min + length * t0, y_min + length * t1
        road_t0 = min(1.0, max(0.0, (ya + MODE_LENGTH * 0.5) / MODE_LENGTH))
        road_t1 = min(1.0, max(0.0, (yb + MODE_LENGTH * 0.5) / MODE_LENGTH))
        road_a = road_start + (road_end - road_start) * road_t0
        road_b = road_start + (road_end - road_start) * road_t1
        bottom_a, bottom_b = road_a - MEDIAN_Z_FIGHT_EPSILON, road_b - MEDIAN_Z_FIGHT_EPSILON
        top_a, top_b = road_a + height, road_b + height
        v0 = index / slices
        v1 = (index + 1) / slices
        mesh.quad(
            (left, ya, top_a), (left, yb, top_b),
            (left, yb, bottom_b), (left, ya, bottom_a),
            uvs=((wall_u_max, v0), (wall_u_max, v1),
                 (wall_u_min, v1), (wall_u_min, v0)),
            uv_region="curb.wall",
        )
        # Curb tops and the centre fill deliberately remain separate faces.
        mesh.quad(
            (left, ya, top_a), (inner_left, ya, top_a),
            (inner_left, yb, top_b), (left, yb, top_b),
            uvs=((upper_u_min, v0), (upper_u_max, v0),
                 (upper_u_max, v1), (upper_u_min, v1)),
            uv_region="curb.upper",
        )
        if inner_right - inner_left > 1e-8:
            mesh.quad(
                (inner_left, ya, top_a), (inner_right, ya, top_a),
                (inner_right, yb, top_b), (inner_left, yb, top_b),
                uvs=((sidewalk_u_min, v0), (sidewalk_u_max, v0),
                     (sidewalk_u_max, v1), (sidewalk_u_min, v1)),
                uv_region="sidewalk.default",
            )
        mesh.quad(
            (inner_right, ya, top_a), (right, ya, top_a),
            (right, yb, top_b), (inner_right, yb, top_b),
            uvs=((upper_u_max, v0), (upper_u_min, v0),
                 (upper_u_min, v1), (upper_u_max, v1)),
            uv_region="curb.upper",
        )
        mesh.quad(
            (right, ya, bottom_a), (right, yb, bottom_b),
            (right, yb, top_b), (right, ya, top_a),
            uvs=((wall_u_min, v0), (wall_u_min, v1),
                 (wall_u_max, v1), (wall_u_max, v0)),
            uv_region="curb.wall",
        )


def _add_median_for_profile(
    mesh, props, median_range, y_min, y_max, road_start, road_end, slices,
) -> str | None:
    if median_range is None:
        return None
    if props.median_with_curb:
        _add_generated_median(
            mesh, median_range, y_min, y_max, road_start, road_end,
            props.median_height, props.median_curb_width, slices,
        )
        return None
    if props.median_mesh is None:
        raise ValueError("Median without generated curbs needs a Median mesh")
    plan = _prepare_median_mesh(
        props.median_mesh,
        props.median_width,
        props.median_height,
        road_start,
        road_end,
        slices,
        props.median_no_split_group,
    )
    _append_median_mesh(mesh, plan)
    return plan.source_name


def _marking_layout(
    props,
) -> tuple[list[tuple[float, str]], list[float], tuple[float, float] | None]:
    _sync_boundaries(props)
    road_lanes = [lane for lane in props.lanes if lane.zone == "ROAD"]
    lane_width = sum(lane.width for lane in road_lanes)
    median_width = props.median_width if props.median_enabled else 0.0
    split_index = median_split_index(road_lanes) if props.median_enabled else -1
    cursor = -(lane_width + median_width) * 0.5
    positions = {}
    median_range = None
    if road_lanes:
        left_id = "boundary-left-carriageway" if props.shoulder_width > 1e-8 else "boundary-left-curb"
        positions[left_id] = cursor
    for boundary_index, (left, right) in enumerate(
        zip(road_lanes, road_lanes[1:]), 1
    ):
        cursor += left.width
        if boundary_index == split_index:
            median_left = cursor
            positions["boundary-median-left"] = median_left
            cursor += median_width
            positions["boundary-median-right"] = cursor
            median_range = (median_left, cursor)
        else:
            positions[f"boundary-{left.lane_id}-{right.lane_id}"] = cursor
    if road_lanes:
        right_id = "boundary-right-carriageway" if props.shoulder_width > 1e-8 else "boundary-right-curb"
        positions[right_id] = (lane_width + median_width) * 0.5
    enabled_markings = [
        (positions[item.boundary_id], item.marking_style)
        for item in props.boundaries
        if item.marking_enabled and item.boundary_id in positions
    ]
    edge_centers = [positions[key] for key in positions if key in {"boundary-left-carriageway", "boundary-left-curb", "boundary-right-carriageway", "boundary-right-curb"}]
    return enabled_markings, edge_centers, median_range


@dataclass(frozen=True)
class _RoadUVSpan:
    x_min: float
    x_max: float
    region_id: str
    reverse: bool = False
    u_range: tuple[float, float] | None = None
    uv_region_id: str | None = None


def _road_uv_spans(props, road_half) -> tuple[_RoadUVSpan, ...]:
    road_lanes = [lane for lane in props.lanes if lane.zone == "ROAD"]
    median_width = props.median_width if props.median_enabled else 0.0
    split_index = median_split_index(road_lanes) if props.median_enabled else -1
    lane_width = sum(lane.width for lane in road_lanes)
    lane_left = -(lane_width + median_width) * 0.5
    lane_right = (lane_width + median_width) * 0.5
    spans = []

    if lane_left > -road_half + 1e-8:
        spans.append(_RoadUVSpan(-road_half, lane_left, "shoulder.default"))
    cursor = lane_left
    for lane_index, lane in enumerate(road_lanes):
        if lane_index == split_index:
            cursor += median_width
        lane_end = cursor + lane.width
        clipped_left = max(cursor, -road_half)
        clipped_right = min(lane_end, road_half)
        if clipped_right > clipped_left + 1e-8:
            spans.append(_RoadUVSpan(clipped_left, clipped_right, "lane.default"))
        cursor = lane_end
    if road_half > lane_right + 1e-8:
        spans.append(_RoadUVSpan(lane_right, road_half, "shoulder.default", True))

    wall_u_max = _texture_region("curb.wall")[1]
    for index, span in enumerate(spans):
        touches_left_curb = abs(span.x_min + road_half) < 1e-8
        touches_right_curb = abs(span.x_max - road_half) < 1e-8
        if not touches_left_curb and not touches_right_curb:
            continue
        composite_id = f"curb.lower+{span.region_id}"
        atlas_name, u_min, _default_u_max = _uv_region_bounds(composite_id)
        manifest = _texture_layout_manifest()
        if atlas_name != "surface":
            raise ValueError(f"{composite_id} must use the surface atlas")
        u_max = u_min + (
            (span.x_max - span.x_min)
            * float(manifest["pixels_per_meter"])
            / float(manifest["atlas_width_px"])
        )
        edge_asphalt_u_max = _texture_region("edge.asphalt")[1]
        if u_max > edge_asphalt_u_max + 1e-8:
            raise ValueError(
                f"{composite_id} width {span.x_max - span.x_min:.3f} m "
                "exceeds the 3.25 m continuous road-edge texture profile"
            )
        if abs(u_min - wall_u_max) > 1e-8:
            raise ValueError(f"{composite_id} does not meet curb.wall")
        spans[index] = _RoadUVSpan(
            span.x_min, span.x_max, span.region_id, span.reverse,
            (u_min, u_max), composite_id,
        )
    return tuple(spans)


def _span_u(span: _RoadUVSpan, x: float) -> float:
    if span.u_range is None:
        u_min, u_max, _ = _texture_region(span.region_id)
    else:
        u_min, u_max = span.u_range
    ratio = (x - span.x_min) / max(span.x_max - span.x_min, 1e-8)
    ratio = min(1.0, max(0.0, ratio))
    if span.reverse:
        ratio = 1.0 - ratio
    return u_min + (u_max - u_min) * ratio


def _road_span_at(spans, x: float) -> _RoadUVSpan:
    for span in spans:
        if span.x_min - 1e-8 <= x <= span.x_max + 1e-8:
            return span
    raise ValueError(f"Road surface X={x} has no UV region")


def _surface_texture_boundaries(spans) -> list[float]:
    return sorted({
        round(value, 9)
        for span in spans
        for value in (span.x_min, span.x_max)
    })


def _segment_boundaries(
    road_half, markings, marking_region_width, texture_boundaries,
    median_range=None,
) -> list[float]:
    half_region = marking_region_width * 0.5
    values = [
        -road_half,
        road_half,
        *(
            boundary for boundary in texture_boundaries
            if not any(
                abs(boundary - center) < half_region - 1e-8
                for center, _style in markings
            )
        ),
    ]
    if median_range is not None:
        values.extend(median_range)
    for center, _style in markings:
        values.extend((max(-road_half, center - half_region), min(road_half, center + half_region)))
    return sorted({round(value, 9) for value in values})


def _node_boundaries(
    props, road_half, edge_centers, texture_boundaries, median_range=None,
) -> list[float]:
    values = [-road_half, 0.0, road_half, *texture_boundaries]
    if median_range is not None:
        values.extend(median_range)
    if props.node_shoulder_bands:
        values.extend(edge_centers)
    return sorted({round(value, 9) for value in values})


def _cross_section_uv_row(t, road_half, total_half, road_start, road_end, sidewalk_start, sidewalk_end):
    road_z = road_start + (road_end - road_start) * t
    sidewalk_z = sidewalk_start + (sidewalk_end - sidewalk_start) * t
    return {
        "road_z": road_z,
        "sidewalk_z": sidewalk_z,
    }


def _add_road_strips(
    mesh, boundaries, markings, marking_width, road_half, total_half, y_min, y_max,
    road_start, road_end, sidewalk_start, sidewalk_end, split_center, slices,
    uv_spans, excluded_range=None,
) -> None:
    for x_min, x_max in zip(boundaries, boundaries[1:]):
        if x_max - x_min < 1e-8:
            continue
        if (
            excluded_range is not None
            and x_min >= excluded_range[0] - 1e-8
            and x_max <= excluded_range[1] + 1e-8
        ):
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
            marking = next((
                (center, style) for center, style in markings
                if abs(midpoint - center) <= marking_width * 0.5 + 1e-8
            ), None)
            kind = "marking" if marking is not None else "surface"
            if marking is not None:
                style = marking[1]
                try:
                    region_id = MARKING_TEXTURE_REGIONS[style]
                except KeyError as error:
                    raise ValueError(f"No texture region for marking style: {style}") from error
                line_u_min, line_u_max, _ = _texture_region(region_id)
                uvs = (
                    (line_u_min, t0), (line_u_max, t0),
                    (line_u_max, t1), (line_u_min, t1),
                )
            else:
                span = _road_span_at(uv_spans, midpoint)
                uvs = (
                    (_span_u(span, x_min), t0), (_span_u(span, x_max), t0),
                    (_span_u(span, x_max), t1), (_span_u(span, x_min), t1),
                )
            mesh.quad(
                (x_min, ya, za), (x_max, ya, za),
                (x_max, yb, zb), (x_min, yb, zb),
                seams, kind, uvs,
                region_id if marking is not None else (
                    span.uv_region_id or span.region_id
                ),
            )


def _add_cross_section_top(
    mesh, boundaries, markings, marking_width, road_half, total_half, y_min, y_max,
    road_start, road_end, sidewalk_start, sidewalk_end, split_center, slices,
    uv_spans, excluded_range=None,
) -> None:
    _add_road_strips(
        mesh, boundaries, markings, marking_width, road_half, total_half, y_min, y_max,
        road_start, road_end, sidewalk_start, sidewalk_end, split_center, slices,
        uv_spans, excluded_range,
    )
    wall_u_min, wall_u_max, _ = _texture_region("curb.wall")
    _atlas_name, sidewalk_u_min, sidewalk_u_max = _uv_region_bounds(
        "sidewalk.default+curb.upper"
    )
    if abs(sidewalk_u_max - wall_u_min) > 1e-8:
        raise ValueError("sidewalk and curb.upper UV do not meet curb.wall")
    for index in range(slices):
        t0, t1 = index / slices, (index + 1) / slices
        ya, yb = y_min + (y_max - y_min) * t0, y_min + (y_max - y_min) * t1
        row0 = _cross_section_uv_row(t0, road_half, total_half, road_start, road_end, sidewalk_start, sidewalk_end)
        row1 = _cross_section_uv_row(t1, road_half, total_half, road_start, road_end, sidewalk_start, sidewalk_end)
        mesh.quad(
            (-total_half, ya, row0["sidewalk_z"]),
            (-road_half, ya, row0["sidewalk_z"]),
            (-road_half, yb, row1["sidewalk_z"]),
            (-total_half, yb, row1["sidewalk_z"]),
            uvs=((sidewalk_u_min, t0), (sidewalk_u_max, t0),
                 (sidewalk_u_max, t1), (sidewalk_u_min, t1)),
            uv_region="sidewalk.default+curb.upper",
        )
        mesh.quad(
            (road_half, ya, row0["sidewalk_z"]),
            (total_half, ya, row0["sidewalk_z"]),
            (total_half, yb, row1["sidewalk_z"]),
            (road_half, yb, row1["sidewalk_z"]),
            uvs=((sidewalk_u_max, t0), (sidewalk_u_min, t0),
                 (sidewalk_u_min, t1), (sidewalk_u_max, t1)),
            uv_region="sidewalk.default+curb.upper",
        )
        if row0["road_z"] != row0["sidewalk_z"] or row1["road_z"] != row1["sidewalk_z"]:
            mesh.quad(
                (-road_half, ya, row0["road_z"]), (-road_half, yb, row1["road_z"]),
                (-road_half, yb, row1["sidewalk_z"]), (-road_half, ya, row0["sidewalk_z"]),
                uvs=(
                    (wall_u_max, t0), (wall_u_max, t1),
                    (wall_u_min, t1), (wall_u_min, t0),
                ),
                uv_region="curb.wall",
            )
            mesh.quad(
                (road_half, ya, row0["sidewalk_z"]), (road_half, yb, row1["sidewalk_z"]),
                (road_half, yb, row1["road_z"]), (road_half, ya, row0["road_z"]),
                uvs=(
                    (wall_u_min, t0), (wall_u_min, t1),
                    (wall_u_max, t1), (wall_u_max, t0),
                ),
                uv_region="curb.wall",
            )


def _add_structure_bottom_region(
    mesh, x_min, x_max, half_width, y_min, y_max, start_z, end_z, slices,
    atlas_name, region_id,
) -> None:
    region_u_min, region_u_max, _ = _atlas_texture_region(atlas_name, region_id)
    for index in range(slices):
        t0, t1 = index / slices, (index + 1) / slices
        ya, yb = y_min + (y_max - y_min) * t0, y_min + (y_max - y_min) * t1
        za, zb = start_z + (end_z - start_z) * t0, start_z + (end_z - start_z) * t1
        ratio0 = (x_min + half_width) / (2.0 * half_width)
        ratio1 = (x_max + half_width) / (2.0 * half_width)
        u0 = region_u_min + (region_u_max - region_u_min) * ratio0
        u1 = region_u_min + (region_u_max - region_u_min) * ratio1
        mesh.quad(
            (x_min, yb, zb), (x_max, yb, zb),
            (x_max, ya, za), (x_min, ya, za),
            kind="structure",
            uvs=((u0, t1), (u1, t1), (u1, t0), (u0, t0)),
            uv_region=region_id,
        )


def _girder_profile(layout, center_x, top_z):
    half_width = layout.width * 0.5
    bottom_z = top_z - layout.depth
    return (
        (center_x - half_width, top_z),
        (center_x - half_width, bottom_z),
        (center_x + half_width, bottom_z),
        (center_x + half_width, top_z),
    )


def _add_open_profile_extrusion(
    mesh, start_profile, end_profile, y_min, y_max, slices,
) -> None:
    for edge_index in range(len(start_profile) - 1):
        region_id = "girder.bottom" if edge_index == 1 else "girder.side"
        u0, u1, _ = _atlas_texture_region("structure", region_id)
        if edge_index == 2:
            u0, u1 = u1, u0
        for slice_index in range(slices):
            t0, t1 = slice_index / slices, (slice_index + 1) / slices
            ya = y_min + (y_max - y_min) * t0
            yb = y_min + (y_max - y_min) * t1
            start_a, start_b = start_profile[edge_index], start_profile[edge_index + 1]
            end_a, end_b = end_profile[edge_index], end_profile[edge_index + 1]
            a0 = (start_a[0], ya, start_a[1] + (end_a[1] - start_a[1]) * t0)
            a1 = (start_a[0], yb, start_a[1] + (end_a[1] - start_a[1]) * t1)
            b1 = (start_b[0], yb, start_b[1] + (end_b[1] - start_b[1]) * t1)
            b0 = (start_b[0], ya, start_b[1] + (end_b[1] - start_b[1]) * t0)
            mesh.quad(
                a0, a1, b1, b0,
                kind="structure",
                uvs=((u0, t0), (u0, t1), (u1, t1), (u1, t0)),
                uv_region=region_id,
            )


def _add_deck_structure(
    mesh, half_width, y_min, y_max, start_z, end_z, depth, slices,
    girder_layout=None, bottom_left=None, bottom_right=None,
    include_left_fascia=True, include_right_fascia=True,
    atlas_name="structure", underside_region="deck.underside",
    fascia_region="elevated.fascia",
) -> None:
    underside_start = start_z - depth
    underside_end = end_z - depth
    bottom_left = -half_width if bottom_left is None else bottom_left
    bottom_right = half_width if bottom_right is None else bottom_right
    if bottom_left >= bottom_right:
        raise ValueError("Elevated edge mesh lowest edges leave no deck underside width")
    cutouts = []
    if girder_layout is not None:
        cutouts = [
            (
                max(bottom_left, center - girder_layout.width * 0.5),
                min(bottom_right, center + girder_layout.width * 0.5),
            )
            for center in girder_layout.centers
            if (
                center + girder_layout.width * 0.5 > bottom_left
                and center - girder_layout.width * 0.5 < bottom_right
            )
        ]

    cursor = bottom_left
    for cutout_start, cutout_end in cutouts:
        if cutout_start > cursor + 1e-8:
            _add_structure_bottom_region(
                mesh, cursor, cutout_start, half_width, y_min, y_max,
                underside_start, underside_end, slices,
                atlas_name, underside_region,
            )
        cursor = max(cursor, cutout_end)
    if cursor < bottom_right - 1e-8:
        _add_structure_bottom_region(
            mesh, cursor, bottom_right, half_width, y_min, y_max,
            underside_start, underside_end, slices,
            atlas_name, underside_region,
        )

    fascia_u = _atlas_texture_region(atlas_name, fascia_region)[:2]
    if include_left_fascia:
        mesh.vertical_strip(
            -half_width, y_min, y_max, underside_start, underside_end,
            start_z, end_z, flip=True, slices=slices, kind="structure",
            u_range=fascia_u, uv_region=fascia_region,
        )
    if include_right_fascia:
        mesh.vertical_strip(
            half_width, y_min, y_max, underside_start, underside_end,
            start_z, end_z, slices=slices, kind="structure",
            u_range=fascia_u, uv_region=fascia_region,
        )

    if girder_layout is not None:
        for center in girder_layout.centers:
            _add_open_profile_extrusion(
                mesh,
                _girder_profile(girder_layout, center, underside_start),
                _girder_profile(girder_layout, center, underside_end),
                y_min,
                y_max,
                slices,
            )


def _add_tunnel_envelope(mesh, half_width, y_min, y_max, side_z, roof_z, slices) -> None:
    wall_u = _atlas_texture_region("tunnel", "tunnel.wall")[:2]
    roof_u = _atlas_texture_region("tunnel", "tunnel.roof")[:2]
    mesh.vertical_strip(-half_width, y_min, y_max, side_z, side_z, roof_z, roof_z, slices=slices, kind="structure", u_range=wall_u, uv_region="tunnel.wall")
    mesh.vertical_strip(half_width, y_min, y_max, side_z, side_z, roof_z, roof_z, flip=True, slices=slices, kind="structure", u_range=wall_u, uv_region="tunnel.wall")
    mesh.horizontal_strip(-half_width, half_width, y_min, y_max, roof_z, roof_z, slices, flip=True, kind="structure", u_range=roof_u, uv_region="tunnel.roof")


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
        lane.vertical_offset = lane_vertical_offset(zone, props.depress_roadway)


def _active_median_width(props) -> float:
    return props.median_width if props.median_enabled else 0.0


def _remove_automatic_parking_lanes(props) -> None:
    for index in range(len(props.lanes) - 1, -1, -1):
        if props.lanes[index].lane_id in AUTO_PARKING_LANE_IDS:
            props.lanes.remove(index)


def _add_automatic_parking_lane(props, lane_id, name, direction, speed_limit):
    lane = props.lanes.add()
    lane.lane_id = lane_id
    lane.name = name
    lane.zone = "ROAD"
    lane.width = props.parking_lane_width
    lane.direction = direction
    lane.lane_type = "PARKING"
    lane.vehicle_type = "CAR"
    lane.speed_limit = speed_limit
    lane.allow_connect = True
    return lane


def _sync_cross_section_state(props):
    """Resolve profile, optional parking lanes, and derived shoulders."""
    global _CROSS_SECTION_SYNCING
    if _CROSS_SECTION_SYNCING:
        return allocate_cross_section(
            props.lanes, props.between_sidewalks_width, _active_median_width(props),
        )
    _CROSS_SECTION_SYNCING = True
    try:
        if (
            hasattr(props, "is_property_set")
            and not props.is_property_set("median_profile")
            and props.median_enabled
        ):
            props.median_profile = "CURB" if props.median_with_curb else "MESH"
        profile = props.median_profile
        props.median_enabled = profile != "NONE"
        props.median_with_curb = profile == "CURB"

        if (
            hasattr(props, "is_property_set")
            and not props.is_property_set("between_sidewalks_width")
        ):
            legacy_lane_width = sum(
                lane.width for lane in props.lanes if lane.zone == "ROAD"
            )
            props.between_sidewalks_width = max(
                legacy_lane_width
                + 2.0 * props.shoulder_width
                + _active_median_width(props),
                0.01,
            )

        base_road_lanes = [
            lane for lane in props.lanes
            if lane.zone == "ROAD" and lane.lane_id not in AUTO_PARKING_LANE_IDS
        ]
        median_width = _active_median_width(props)
        create_parking = (
            props.roadside_use == "PARKING"
            and base_road_lanes
            and parking_fits(
                base_road_lanes,
                props.between_sidewalks_width,
                median_width,
                props.parking_lane_width,
            )
        )
        existing_parking = {
            lane.lane_id: lane for lane in props.lanes
            if lane.lane_id in AUTO_PARKING_LANE_IDS
        }
        road_lane_ids = [lane.lane_id for lane in props.lanes if lane.zone == "ROAD"]
        parking_is_current = bool(
            create_parking
            and len(existing_parking) == 2
            and existing_parking["lane-auto-parking-left"].zone == "ROAD"
            and existing_parking["lane-auto-parking-right"].zone == "ROAD"
            and existing_parking["lane-auto-parking-left"].lane_type == "PARKING"
            and existing_parking["lane-auto-parking-right"].lane_type == "PARKING"
            and existing_parking["lane-auto-parking-left"].vehicle_type == "CAR"
            and existing_parking["lane-auto-parking-right"].vehicle_type == "CAR"
            and abs(
                existing_parking["lane-auto-parking-left"].width
                - props.parking_lane_width
            ) <= 1e-6
            and abs(
                existing_parking["lane-auto-parking-right"].width
                - props.parking_lane_width
            ) <= 1e-6
            and existing_parking["lane-auto-parking-left"].direction
                == base_road_lanes[0].direction
            and existing_parking["lane-auto-parking-right"].direction
                == base_road_lanes[-1].direction
            and road_lane_ids[0] == "lane-auto-parking-left"
            and road_lane_ids[-1] == "lane-auto-parking-right"
        )
        if not parking_is_current:
            _remove_automatic_parking_lanes(props)
        if create_parking and not parking_is_current:
            left_reference = base_road_lanes[0]
            right_reference = base_road_lanes[-1]
            left = _add_automatic_parking_lane(
                props, "lane-auto-parking-left", "Parking L",
                left_reference.direction, left_reference.speed_limit,
            )
            _add_automatic_parking_lane(
                props, "lane-auto-parking-right", "Parking R",
                right_reference.direction, right_reference.speed_limit,
            )
            first_road_index = next(
                index for index, lane in enumerate(props.lanes)
                if lane.zone == "ROAD" and lane.as_pointer() != left.as_pointer()
            )
            props.lanes.move(len(props.lanes) - 2, first_road_index)

        allocation = allocate_cross_section(
            props.lanes, props.between_sidewalks_width, median_width,
        )
        if abs(props.shoulder_width - allocation.shoulder_width) > 1e-6:
            props.shoulder_width = allocation.shoulder_width
        _ensure_lane_ids(props)
        _sync_boundaries(props)
        return allocation
    finally:
        _CROSS_SECTION_SYNCING = False


def _cross_section_allocation(props):
    """Read the current width budget without mutating Blender properties."""
    return allocate_cross_section(
        props.lanes,
        props.between_sidewalks_width,
        _active_median_width(props),
    )


def _cross_section_warning(props) -> str | None:
    allocation = _cross_section_allocation(props)
    if allocation.overflow > 1e-8:
        return (
            f"Cross-section exceeds the sidewalk span by "
            f"{allocation.overflow:.2f} m; explicit lane and median widths were kept"
        )
    base_lanes = [
        lane for lane in props.lanes
        if lane.zone == "ROAD" and lane.lane_id not in AUTO_PARKING_LANE_IDS
    ]
    if (
        props.roadside_use == "PARKING"
        and not parking_fits(
            base_lanes, props.between_sidewalks_width,
            _active_median_width(props), props.parking_lane_width,
        )
    ):
        return (
            f"Parking needs {props.parking_lane_width:.2f} m on both sides; "
            "the remaining width stays as shoulders"
        )
    return None


def _sync_lane_derived_values(props) -> None:
    sidewalk_counts = {
        zone: sum(lane.zone == zone for lane in props.lanes)
        for zone in ("LEFT_SIDEWALK", "RIGHT_SIDEWALK")
    }
    for lane in props.lanes:
        if lane.zone in {"LEFT_SIDEWALK", "RIGHT_SIDEWALK"}:
            if lane.direction != "BOTH":
                lane.direction = "BOTH"
            if lane.lane_type != "PEDESTRIAN":
                lane.lane_type = "PEDESTRIAN"
            if lane.vehicle_type != "NONE":
                lane.vehicle_type = "NONE"
            derived_width = sidewalk_lane_width(
                props.sidewalk_width, sidewalk_counts[lane.zone]
            )
            if abs(lane.width - derived_width) > 1e-6:
                lane.width = derived_width
        elif lane.lane_type == "VEHICLE" and lane.vehicle_type == "NONE":
            lane.vehicle_type = "CAR"
        derived_offset = lane_vertical_offset(lane.zone, props.depress_roadway)
        if abs(lane.vertical_offset - derived_offset) > 1e-6:
            lane.vertical_offset = derived_offset


def _on_sidewalk_width_update(props, context) -> None:
    _sync_lane_derived_values(props)
    _schedule_live_preview(props, context)


def _existing_preview_modes() -> tuple[str, ...]:
    return tuple(
        mode for mode, _, _ in MODE_ITEMS
        if (collection := bpy.data.collections.get(f"CS1_ROAD_{mode}")) is not None
        and bool(collection.objects)
    )


def _schedule_live_preview(props, context) -> None:
    if _LIVE_PREVIEW_REBUILDING:
        return
    scene = getattr(context, "scene", None)
    scene_props = active_road(scene) if scene is not None else None
    if (
        scene_props is None
        or scene_props.as_pointer() != props.as_pointer()
    ):
        return
    modes = _existing_preview_modes()
    if not modes:
        return
    scene_key = scene.as_pointer()
    now = time.monotonic()
    pending = _LIVE_PREVIEW_PENDING.get(scene_key)
    if pending is None:
        _LIVE_PREVIEW_PENDING[scene_key] = {
            "live_due": now + _LIVE_PREVIEW_DELAY,
            "settle_due": now + _LIVE_PREVIEW_SETTLE_DELAY,
            "last_live": 0.0,
            "dirty": True,
            "active_mode": props.mode,
            "modes": modes,
        }
    else:
        pending_modes = set(pending["modes"]) | set(modes)
        pending["modes"] = tuple(
            mode for mode, _, _ in MODE_ITEMS if mode in pending_modes
        )
        pending["active_mode"] = props.mode
        pending["settle_due"] = now + _LIVE_PREVIEW_SETTLE_DELAY
        if not pending["dirty"]:
            pending["live_due"] = max(
                now, pending["last_live"] + _LIVE_PREVIEW_DELAY
            )
        pending["dirty"] = True
    if not bpy.app.timers.is_registered(_live_preview_timer):
        bpy.app.timers.register(_live_preview_timer, first_interval=_LIVE_PREVIEW_DELAY)


def _live_preview_timer():
    if not _LIVE_PREVIEW_PENDING:
        return None
    now = time.monotonic()
    next_delay = None
    scenes = {scene.as_pointer(): scene for scene in bpy.data.scenes}
    for scene_key, pending in tuple(_LIVE_PREVIEW_PENDING.items()):
        scene = scenes.get(scene_key)
        if scene is None:
            _LIVE_PREVIEW_PENDING.pop(scene_key, None)
            continue
        try:
            if pending["dirty"] and pending["live_due"] <= now:
                mode = pending["active_mode"]
                collection = bpy.data.collections.get(f"CS1_ROAD_{mode}")
                if collection is not None and collection.objects:
                    build_mode(scene, mode)
                pending["dirty"] = False
                pending["last_live"] = time.monotonic()
            if pending["settle_due"] <= now:
                for mode in pending["modes"]:
                    if mode == pending["active_mode"] and not pending["dirty"]:
                        continue
                    collection = bpy.data.collections.get(f"CS1_ROAD_{mode}")
                    if collection is not None and collection.objects:
                        build_mode(scene, mode)
                _LIVE_PREVIEW_PENDING.pop(scene_key, None)
        except (ValueError, TypeError) as error:
            _LIVE_PREVIEW_PENDING.pop(scene_key, None)
            print(f"CS1 Road Builder live preview: {error}")
        pending = _LIVE_PREVIEW_PENDING.get(scene_key)
        if pending is not None:
            deadlines = [pending["settle_due"]]
            if pending["dirty"]:
                deadlines.append(pending["live_due"])
            delay = max(min(deadlines) - time.monotonic(), 0.01)
            next_delay = delay if next_delay is None else min(next_delay, delay)
    if _LIVE_PREVIEW_PENDING:
        return next_delay or _LIVE_PREVIEW_DELAY
    return None


def _on_geometry_update(props, context) -> None:
    _schedule_live_preview(props, context)


def _on_road_color_update(props, context) -> None:
    material = bpy.data.materials.get(SHARED_SURFACE_MATERIAL)
    if material is not None:
        material.diffuse_color = (*tuple(props.road_color), 1.0)


def _on_cross_section_update(props, context) -> None:
    _sync_cross_section_state(props)
    _schedule_live_preview(props, context)


def _on_depress_roadway_update(props, context) -> None:
    _sync_lane_derived_values(props)
    _schedule_live_preview(props, context)


def _on_lane_width_update(lane, context) -> None:
    if lane.zone == "ROAD":
        scene = getattr(context, "scene", None)
        props = active_road(scene) if scene is not None else None
        if props is not None:
            _sync_cross_section_state(props)
            _schedule_live_preview(props, context)


def _on_lane_direction_update(lane, context) -> None:
    scene = getattr(context, "scene", None)
    props = active_road(scene) if scene is not None else None
    if props is not None:
        _sync_cross_section_state(props)
        _schedule_live_preview(props, context)


def _on_boundary_marking_update(boundary, context) -> None:
    scene = getattr(context, "scene", None)
    props = active_road(scene) if scene is not None else None
    if props is not None:
        _schedule_live_preview(props, context)


def _on_marking_rules_update(props, context) -> None:
    _sync_boundaries(props)
    props.imt_center_line_yellow = props.center_line_style == "SOLID_YELLOW"
    _schedule_live_preview(props, context)


def _on_imt_appearance_preset_update(props, context) -> None:
    global _IMT_PRESET_APPLYING
    if props.imt_appearance_preset != "JP_WEATHERED":
        return
    _IMT_PRESET_APPLYING = True
    try:
        props.imt_white_color = (245 / 255, 245 / 255, 235 / 255, 1.0)
        props.imt_yellow_color = (1.0, 0.72, 0.0, 1.0)
        props.imt_texture = 0.25
        props.imt_cracks_density = 0.70
        props.imt_cracks_scale = 0.40
        props.imt_voids_density = 0.20
        props.imt_voids_scale = 1.0
        props.imt_crosswalk_width = 3.0
        props.imt_crosswalk_dash_length = 0.45
        props.imt_crosswalk_gap_length = 0.55
        props.imt_crosswalk_offset = 0.40
        props.imt_stop_line_width = 0.30
        props.imt_dash_length = 6.0
        props.imt_dash_gap = 10.0
    finally:
        _IMT_PRESET_APPLYING = False


def _on_imt_appearance_value_update(props, context) -> None:
    if not _IMT_PRESET_APPLYING and props.imt_appearance_preset != "CUSTOM":
        props.imt_appearance_preset = "CUSTOM"


def _load_imt_appearance(props, source) -> None:
    global _IMT_PRESET_APPLYING
    preset = _enum_value(
        str(source.get("preset", props.imt_appearance_preset)),
        IMT_APPEARANCE_PRESET_ITEMS, props.imt_appearance_preset,
    )
    if preset == "JP_WEATHERED":
        props.imt_appearance_preset = preset
        return
    _IMT_PRESET_APPLYING = True
    try:
        props.roadside_lines = bool(
            source.get("roadside_lines", props.roadside_lines)
        )
        props.lane_separator_style = _enum_value(
            str(source.get("lane_separator_style", props.lane_separator_style)),
            LANE_SEPARATOR_STYLE_ITEMS, props.lane_separator_style,
        )
        center_fallback = (
            "SOLID_YELLOW"
            if source.get("center_line_yellow", False)
            else props.center_line_style
        )
        props.center_line_style = _enum_value(
            str(source.get("center_line_style", center_fallback)),
            CENTER_LINE_STYLE_ITEMS, center_fallback,
        )
        props.imt_appearance_preset = preset
        props.imt_white_color = tuple(source.get("white_color", props.imt_white_color))
        props.imt_yellow_color = tuple(source.get("yellow_color", props.imt_yellow_color))
        props.imt_center_line_yellow = props.center_line_style == "SOLID_YELLOW"
        props.imt_texture = float(source.get("texture", props.imt_texture))
        cracks = source.get("cracks", [props.imt_cracks_density, props.imt_cracks_scale])
        voids = source.get("voids", [props.imt_voids_density, props.imt_voids_scale])
        props.imt_cracks_density, props.imt_cracks_scale = map(float, cracks)
        props.imt_voids_density, props.imt_voids_scale = map(float, voids)
        props.imt_crosswalk_width = float(source.get("crosswalk_width", props.imt_crosswalk_width))
        props.imt_crosswalk_dash_length = float(source.get("crosswalk_dash_length", props.imt_crosswalk_dash_length))
        props.imt_crosswalk_gap_length = float(source.get("crosswalk_gap_length", props.imt_crosswalk_gap_length))
        props.imt_crosswalk_offset = float(source.get("crosswalk_offset", props.imt_crosswalk_offset))
        props.imt_stop_line_width = float(source.get("stop_line_width", props.imt_stop_line_width))
        props.imt_dash_length = float(source.get("dash_length", props.imt_dash_length))
        props.imt_dash_gap = float(source.get("dash_gap", props.imt_dash_gap))
    finally:
        _IMT_PRESET_APPLYING = False


def _ensure_default_lanes(props) -> None:
    if not props.lanes:
        _add_default_lanes(props)
    _ensure_lane_ids(props)
    _sync_cross_section_state(props)


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
    return strip_id(lane)


def _expected_boundaries(props):
    try:
        return expected_boundaries(
            props.lanes, props.shoulder_width, props.median_enabled,
        )
    except ValueError:
        # Lane import and row editing pass through temporary one-direction
        # states.  Keep the UI list usable there; build/export still validate
        # the requested median through median_split_index.
        return expected_boundaries(props.lanes, props.shoulder_width, False)


def _sync_boundaries(props, legacy_edge_lines=None, legacy_lane_lines=None) -> None:
    _ensure_lane_ids(props)
    previous = {
        item.boundary_id: {
            "boundary_role": item.role,
            "left_strip_id": item.left_strip_id,
            "right_strip_id": item.right_strip_id,
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
        same_topology = bool(
            saved
            and saved["boundary_role"] == role
            and saved["left_strip_id"] == left_strip
            and saved["right_strip_id"] == right_strip
        )
        item.role = role
        item.marking_enabled = (
            saved["enabled"] if same_topology else default_enabled
        )
        role_is_unchanged = bool(
            same_topology and saved["role"] == default_marking_role
        )
        item.marking_role = default_marking_role
        item.marking_style = saved["style"] if role_is_unchanged else (
            "DASHED_WHITE"
            if default_marking_role == "CENTER_LINE"
            else "SOLID_WHITE"
        )
        if legacy_edge_lines is not None and item.marking_role == "CARRIAGEWAY_EDGE":
            item.marking_enabled = bool(legacy_edge_lines)
        if legacy_lane_lines is not None and item.role == "LANE_DIVIDER":
            item.marking_enabled = bool(legacy_lane_lines)
        item.marking_enabled, item.marking_style = marking_rule(
            item.role,
            item.marking_role,
            props.roadside_lines,
            props.lane_separator_style,
            props.center_line_style,
            default_enabled,
        )


def _initialize_scene_lanes():
    """Initialize defaults after Blender leaves its restricted register context."""
    try:
        scenes = list(bpy.data.scenes)
    except AttributeError:
        return 0.1
    for scene in scenes:
        legacy = scene.cs1_road_builder
        _ensure_default_lanes(legacy)
        if not scene.cs1_roads:
            road = scene.cs1_roads.add()
            _copy_property_group(legacy, road)
            scene.cs1_active_road_index = 0
        _ensure_default_lanes(active_road(scene))
    return None


def active_road(scene):
    roads = scene.cs1_roads
    if not roads:
        return scene.cs1_road_builder
    index = max(0, min(scene.cs1_active_road_index, len(roads) - 1))
    return roads[index]


def _copy_property_group(source, target) -> None:
    global _LIVE_PREVIEW_REBUILDING
    previous = _LIVE_PREVIEW_REBUILDING
    _LIVE_PREVIEW_REBUILDING = True
    try:
        for definition in source.bl_rna.properties:
            name = definition.identifier
            if name in {"rna_type", "lanes", "boundaries"} or definition.is_readonly:
                continue
            try:
                setattr(target, name, getattr(source, name))
            except (AttributeError, TypeError, ValueError):
                pass
        for collection_name in ("lanes", "boundaries"):
            source_items = getattr(source, collection_name)
            target_items = getattr(target, collection_name)
            target_items.clear()
            for source_item in source_items:
                target_item = target_items.add()
                for definition in source_item.bl_rna.properties:
                    name = definition.identifier
                    if name == "rna_type" or definition.is_readonly:
                        continue
                    try:
                        setattr(target_item, name, getattr(source_item, name))
                    except (AttributeError, TypeError, ValueError):
                        pass
    finally:
        _LIVE_PREVIEW_REBUILDING = previous


def _cross_section(props) -> tuple[float, float, float]:
    allocation = _cross_section_allocation(props)
    return cross_section_widths(
        props.lanes, allocation.shoulder_width, props.sidewalk_width,
        props.median_width if props.median_enabled else 0.0,
    )


def _lane_positions(props):
    roadway_lanes = [lane for lane in props.lanes if lane.zone == "ROAD"]
    median_width = props.median_width if props.median_enabled else 0.0
    split_index = median_split_index(roadway_lanes) if props.median_enabled else -1
    cursor = -(sum(lane.width for lane in roadway_lanes) + median_width) * 0.5
    positions = {}
    for index, lane in enumerate(roadway_lanes):
        if index == split_index:
            cursor += median_width
        positions[lane.as_pointer()] = cursor + lane.width * 0.5
        cursor += lane.width
    roadway_width, _, total_half = _cross_section(props)
    for zone, start in (("LEFT_SIDEWALK", -total_half), ("RIGHT_SIDEWALK", roadway_width * 0.5)):
        cursor = start
        for lane in (item for item in props.lanes if item.zone == zone):
            positions[lane.as_pointer()] = cursor + lane.width * 0.5
            cursor += lane.width
    return [(lane, positions.get(lane.as_pointer(), 0.0)) for lane in props.lanes]


def _build_mode_impl(scene: bpy.types.Scene, mode: str) -> list[bpy.types.Object]:
    props = active_road(scene)
    _sync_cross_section_state(props)
    _sync_lane_derived_values(props)
    roadway_width, _, total_half = _cross_section(props)
    custom_edge_segments = None
    custom_edge_nodes = None
    if mode == "elevated":
        edge_source = props.elevated_edge_mesh
        if edge_source is not None:
            side_z = 0.0
            group_name = props.elevated_edge_no_split_group
            half_width, surface_z, depth = total_half, side_z, props.deck_depth
            custom_edge_segments = (
                _prepare_elevated_edge_mesh(
                    edge_source, "left", -half_width, surface_z, depth,
                    SEGMENT_SLICES, group_name,
                ),
                _prepare_elevated_edge_mesh(
                    edge_source, "right", half_width, surface_z, depth,
                    SEGMENT_SLICES, group_name,
                ),
            )
            custom_edge_nodes = (
                _prepare_elevated_edge_mesh(
                    edge_source, "left", -half_width, surface_z, depth,
                    NODE_SLICES, group_name,
                ),
                _prepare_elevated_edge_mesh(
                    edge_source, "right", half_width, surface_z, depth,
                    NODE_SLICES, group_name,
                ),
            )
            if custom_edge_segments[0].connector_x >= custom_edge_segments[1].connector_x:
                raise ValueError("Elevated edge mesh lowest edges cross at the deck underside")

    collection = _mode_collection(scene, mode)
    _clear_collection(collection)
    material = _material(
        props.marking_paint_width, props.marking_region_width, props.road_color,
    )
    structure_material = _structure_material()
    tunnel_material = _tunnel_material()
    road_half = roadway_width * 0.5
    configured_markings, edge_centers, median_range = _marking_layout(props)
    segment_markings = configured_markings if props.line_mesh_enabled else []
    road_uv_spans = _road_uv_spans(props, road_half)
    texture_boundaries = _surface_texture_boundaries(road_uv_spans)
    segment_boundaries = _segment_boundaries(
        road_half, segment_markings, props.marking_region_width,
        texture_boundaries, median_range,
    )
    node_boundaries = _node_boundaries(
        props, road_half, edge_centers, texture_boundaries, median_range,
    )
    node_markings = []
    curb_rise = roadway_depression(props.depress_roadway)
    props.half_width = total_half
    y_min, y_max = -MODE_LENGTH * 0.5, MODE_LENGTH * 0.5
    segment_mesh, node_mesh = _SurfaceMesh(), _SurfaceMesh()
    girder_layout = None

    if mode == "basic":
        segment_z = -curb_rise
        # A depressed road already meets the recessed node surface, so its
        # regular node stays level.  Only a flush road needs the preview
        # profile which descends to the shared recessed node surface.
        far_z = segment_z if props.depress_roadway else -ROADWAY_DEPRESSION
        _add_cross_section_top(segment_mesh, segment_boundaries, segment_markings, props.marking_region_width, road_half, total_half, y_min, y_max, segment_z, segment_z, 0.0, 0.0, False, SEGMENT_SLICES, road_uv_spans, median_range)
        _add_cross_section_top(node_mesh, node_boundaries, node_markings, props.marking_region_width, road_half, total_half, y_min, y_max, segment_z, far_z, 0.0, 0.0, True, NODE_SLICES, road_uv_spans, median_range)
        _add_median_for_profile(segment_mesh, props, median_range, y_min, y_max, segment_z, segment_z, SEGMENT_SLICES)
        _add_median_for_profile(node_mesh, props, median_range, y_min, y_max, segment_z, far_z, NODE_SLICES)
    elif mode == "elevated":
        road_z, side_z = -curb_rise, 0.0
        girder_layout = plan_main_girders(total_half * 2.0)
        _add_cross_section_top(segment_mesh, segment_boundaries, segment_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_z, road_z, side_z, side_z, False, SEGMENT_SLICES, road_uv_spans, median_range)
        _add_cross_section_top(node_mesh, node_boundaries, node_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_z, road_z, side_z, side_z, True, NODE_SLICES, road_uv_spans, median_range)
        _add_median_for_profile(segment_mesh, props, median_range, y_min, y_max, road_z, road_z, SEGMENT_SLICES)
        _add_median_for_profile(node_mesh, props, median_range, y_min, y_max, road_z, road_z, NODE_SLICES)
        if custom_edge_segments is None:
            _add_deck_structure(segment_mesh, total_half, y_min, y_max, side_z, side_z, props.deck_depth, SEGMENT_SLICES, girder_layout)
            _add_deck_structure(node_mesh, total_half, y_min, y_max, side_z, side_z, props.deck_depth, NODE_SLICES)
        else:
            left_segment, right_segment = custom_edge_segments
            left_node, right_node = custom_edge_nodes
            _add_deck_structure(
                segment_mesh, total_half, y_min, y_max, side_z, side_z,
                props.deck_depth, SEGMENT_SLICES, girder_layout,
                left_segment.connector_x, right_segment.connector_x, False, False,
            )
            _add_deck_structure(
                node_mesh, total_half, y_min, y_max, side_z, side_z,
                props.deck_depth, NODE_SLICES, None,
                left_node.connector_x, right_node.connector_x, False, False,
            )
            _append_elevated_edge(segment_mesh, left_segment)
            _append_elevated_edge(segment_mesh, right_segment)
            _append_elevated_edge(node_mesh, left_node)
            _append_elevated_edge(node_mesh, right_node)
    elif mode == "bridge":
        road_z, side_z = -curb_rise, 0.0
        girder_layout = plan_main_girders(total_half * 2.0)
        _add_cross_section_top(segment_mesh, segment_boundaries, segment_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_z, road_z, side_z, side_z, False, SEGMENT_SLICES, road_uv_spans, median_range)
        _add_cross_section_top(node_mesh, node_boundaries, node_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_z, road_z, side_z, side_z, True, NODE_SLICES, road_uv_spans, median_range)
        _add_median_for_profile(segment_mesh, props, median_range, y_min, y_max, road_z, road_z, SEGMENT_SLICES)
        _add_median_for_profile(node_mesh, props, median_range, y_min, y_max, road_z, road_z, NODE_SLICES)
        _add_deck_structure(segment_mesh, total_half, y_min, y_max, side_z, side_z, props.bridge_deck_depth, SEGMENT_SLICES, girder_layout, fascia_region="bridge.fascia")
        _add_deck_structure(node_mesh, total_half, y_min, y_max, side_z, side_z, props.bridge_deck_depth, NODE_SLICES, fascia_region="bridge.fascia")
    elif mode == "slope":
        road_start = road_end = -curb_rise
        side_start = side_end = 0.0
        _add_cross_section_top(segment_mesh, segment_boundaries, segment_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_start, road_end, side_start, side_end, False, SEGMENT_SLICES, road_uv_spans, median_range)
        _add_cross_section_top(node_mesh, node_boundaries, node_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_end, road_end, side_end, side_end, True, NODE_SLICES, road_uv_spans, median_range)
        _add_median_for_profile(segment_mesh, props, median_range, y_min, y_max, road_start, road_end, SEGMENT_SLICES)
        _add_median_for_profile(node_mesh, props, median_range, y_min, y_max, road_end, road_end, NODE_SLICES)
        _add_deck_structure(segment_mesh, total_half, y_min, y_max, side_start, side_end, props.deck_depth, SEGMENT_SLICES, atlas_name="tunnel", underside_region="tunnel.roof", fascia_region="tunnel.wall")
        _add_deck_structure(node_mesh, total_half, y_min, y_max, side_end, side_end, props.deck_depth, NODE_SLICES, atlas_name="tunnel", underside_region="tunnel.roof", fascia_region="tunnel.wall")
    else:
        road_z = -curb_rise
        side_z, roof_z = 0.0, road_z + props.tunnel_clearance
        _add_cross_section_top(segment_mesh, segment_boundaries, segment_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_z, road_z, side_z, side_z, False, SEGMENT_SLICES, road_uv_spans, median_range)
        _add_cross_section_top(node_mesh, node_boundaries, node_markings, props.marking_region_width, road_half, total_half, y_min, y_max, road_z, road_z, side_z, side_z, True, NODE_SLICES, road_uv_spans, median_range)
        _add_median_for_profile(segment_mesh, props, median_range, y_min, y_max, road_z, road_z, SEGMENT_SLICES)
        _add_median_for_profile(node_mesh, props, median_range, y_min, y_max, road_z, road_z, NODE_SLICES)
        _add_tunnel_envelope(segment_mesh, total_half, y_min, y_max, side_z, roof_z, SEGMENT_SLICES)
        _add_tunnel_envelope(node_mesh, total_half, y_min, y_max, side_z, roof_z, NODE_SLICES)

    segment_materials = (material,)
    segment_indices = {}
    node_materials = (material,)
    node_indices = {}
    if mode != "basic":
        secondary_material = (
            tunnel_material if mode in {"slope", "tunnel"} else structure_material
        )
        segment_materials += (secondary_material,)
        segment_indices["structure"] = 1
        node_materials += (secondary_material,)
        node_indices["structure"] = 1
    segment = segment_mesh.create(
        collection, f"{mode}_segment", segment_materials, segment_indices,
    )
    node = node_mesh.create(
        collection, f"{mode}_node", node_materials, node_indices,
    )
    mode_index = next(index for index, item in enumerate(MODE_ITEMS) if item[0] == mode)
    preview_z = mode_index * MODE_PREVIEW_SPACING
    segment.location = (0.0, -MODE_LENGTH * 0.5, preview_z)
    node.location = (0.0, MODE_LENGTH * 0.5, preview_z)
    for obj, part in ((segment, "segment"), (node, "node")):
        obj["cs1_road_mode"], obj["cs1_road_name"], obj["cs1_road_part"] = mode, props.road_name, part
        obj["cs1_local_length"] = MODE_LENGTH
        obj["cs1_longitudinal_slices"] = SEGMENT_SLICES if part == "segment" else NODE_SLICES
    node["cs1_center_split"] = True
    if props.median_enabled:
        for obj in (segment, node):
            obj["cs1_median_width"] = props.median_width
            obj["cs1_median_height"] = props.median_height
            obj["cs1_median_with_curb"] = props.median_with_curb
            if props.median_mesh is not None and not props.median_with_curb:
                obj["cs1_median_source"] = props.median_mesh.name
    if girder_layout is not None:
        segment["cs1_geometry_groups"] = (
            "surface,deck,custom_edge,girder"
            if custom_edge_segments is not None
            else "surface,deck,fascia,girder"
        )
        segment["cs1_girder_count"] = girder_layout.count
        segment["cs1_girder_centers"] = list(girder_layout.centers)
        segment["cs1_girder_spacing"] = girder_layout.spacing
        segment["cs1_girder_outer_offset"] = girder_layout.outer_offset
        segment["cs1_girder_reference_span"] = girder_layout.reference_span
        segment["cs1_girder_standard"] = girder_layout.standard_name
    if custom_edge_segments is not None:
        segment["cs1_elevated_edge_source"] = custom_edge_segments[0].source_name
        segment["cs1_elevated_left_edge_mirrored"] = custom_edge_segments[0].mirrored
        segment["cs1_elevated_edge_no_split_group"] = props.elevated_edge_no_split_group
        node["cs1_elevated_edge_source"] = custom_edge_nodes[0].source_name
        node["cs1_elevated_left_edge_mirrored"] = custom_edge_nodes[0].mirrored
    return [segment, node]


def build_mode(scene: bpy.types.Scene, mode: str) -> list[bpy.types.Object]:
    global _LIVE_PREVIEW_REBUILDING
    previous = _LIVE_PREVIEW_REBUILDING
    _LIVE_PREVIEW_REBUILDING = True
    try:
        return _build_mode_impl(scene, mode)
    finally:
        _LIVE_PREVIEW_REBUILDING = previous


class CS1RoadLane(PropertyGroup):
    lane_id: StringProperty(name="Lane ID")
    zone: EnumProperty(
        name="Kind", items=LANE_ZONE_ITEMS, default="ROAD",
        update=_on_lane_zone_update,
    )
    width: FloatProperty(
        name="Width", default=3.0, min=0.05, max=16.0,
        unit="LENGTH", update=_on_lane_width_update,
    )
    direction: EnumProperty(
        name="Direction", items=DIRECTION_ITEMS, default="FORWARD",
        update=_on_lane_direction_update,
    )
    lane_type: EnumProperty(
        name="Lane type", items=LANE_TYPE_ITEMS, default="VEHICLE", options={"HIDDEN"},
    )
    vehicle_type: EnumProperty(
        name="Vehicle", items=_vehicle_type_items_for_lane,
    )
    speed_limit: FloatProperty(name="Speed limit", default=1.0, min=0.0, max=10.0)
    vertical_offset: FloatProperty(
        name="Derived vertical offset", default=0.0, min=-16.0, max=16.0,
        unit="LENGTH", options={"HIDDEN"},
    )
    stop_offset: FloatProperty(
        name="Stop offset", default=0.0, min=-32.0, max=32.0,
        unit="LENGTH", options={"HIDDEN"},
    )
    allow_connect: BoolProperty(name="Allow connect", default=True, options={"HIDDEN"})


class CS1RoadBoundary(PropertyGroup):
    boundary_id: StringProperty(name="Boundary ID")
    left_strip_id: StringProperty(name="Left strip ID")
    right_strip_id: StringProperty(name="Right strip ID")
    role: EnumProperty(name="Boundary role", items=BOUNDARY_ROLE_ITEMS, default="LANE_DIVIDER")
    marking_enabled: BoolProperty(
        name="Marking", default=False, update=_on_boundary_marking_update,
    )
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
    between_sidewalks_width: FloatProperty(
        name="Between sidewalks", default=7.0, min=0.01, max=128.0,
        unit="LENGTH", update=_on_cross_section_update,
        description="Fixed horizontal span between the two sidewalk surfaces",
    )
    shoulder_width: FloatProperty(
        name="Derived shoulder width", default=0.5, min=0.0, max=64.0,
        unit="LENGTH", options={"HIDDEN"},
    )
    roadside_use: EnumProperty(
        name="Remaining width", items=ROADSIDE_USE_ITEMS, default="SHOULDER",
        update=_on_cross_section_update,
    )
    parking_lane_width: FloatProperty(
        name="Parking lane width", default=PARKING_LANE_DEFAULT_WIDTH,
        min=1.0, max=5.0, unit="LENGTH", update=_on_cross_section_update,
    )
    sidewalk_width: FloatProperty(
        name="Sidewalk width", default=2.5, min=0.0, max=16.0,
        unit="LENGTH", update=_on_sidewalk_width_update,
    )
    median_enabled: BoolProperty(
        name="Median", default=False, options={"HIDDEN"},
    )
    median_profile: EnumProperty(
        name="Median", items=MEDIAN_PROFILE_ITEMS, default="NONE",
        update=_on_cross_section_update,
    )
    median_width: FloatProperty(
        name="Median width", default=1.0, min=0.1, max=32.0,
        unit="LENGTH", update=_on_cross_section_update,
    )
    median_height: FloatProperty(
        name="Height above roadway", default=0.15, min=0.01, max=4.0,
        unit="LENGTH", update=_on_geometry_update,
    )
    median_with_curb: BoolProperty(
        name="Generated curb surround", default=True,
        options={"HIDDEN"},
    )
    median_curb_width: FloatProperty(
        name="Curb top width", default=0.15, min=0.01, max=2.0,
        unit="LENGTH", update=_on_geometry_update,
    )
    median_mesh: PointerProperty(
        name="Median mesh", type=bpy.types.Object,
        poll=_mesh_object_poll, update=_on_geometry_update,
    )
    median_no_split_group: StringProperty(
        name="No-split group", default="CS1_NO_SPLIT", update=_on_geometry_update,
    )
    depress_roadway: BoolProperty(
        name=f"Lower roadway {ROADWAY_DEPRESSION:.2f} m", default=True,
        update=_on_depress_roadway_update,
    )
    node_min_corner_offset: FloatProperty(
        name="Min corner offset", default=0.0, min=0.0, max=128.0,
        unit="LENGTH",
        description="CS1 NetInfo.m_minCornerOffset; increases node corner smoothing",
    )
    road_color: FloatVectorProperty(
        name="Road color", subtype="COLOR_GAMMA", size=3,
        default=DEFAULT_ROAD_COLOR, min=0.0, max=1.0,
        description="Road color applied through the surface Road mask in every mode",
        update=_on_road_color_update,
    )
    roadside_lines: BoolProperty(
        name="Roadside lines", default=True, update=_on_marking_rules_update,
    )
    lane_separator_style: EnumProperty(
        name="Lane separators", items=LANE_SEPARATOR_STYLE_ITEMS,
        default="DASHED_WHITE", update=_on_marking_rules_update,
    )
    center_line_style: EnumProperty(
        name="Center line", items=CENTER_LINE_STYLE_ITEMS,
        default="DASHED_WHITE", update=_on_marking_rules_update,
    )
    imt_appearance_preset: EnumProperty(
        name="Appearance preset", items=IMT_APPEARANCE_PRESET_ITEMS,
        default="JP_WEATHERED", update=_on_imt_appearance_preset_update,
    )
    imt_white_color: FloatVectorProperty(
        name="White paint", subtype="COLOR_GAMMA", size=4,
        default=(245 / 255, 245 / 255, 235 / 255, 1.0), min=0.0, max=1.0,
        update=_on_imt_appearance_value_update,
    )
    imt_yellow_color: FloatVectorProperty(
        name="Yellow paint", subtype="COLOR_GAMMA", size=4,
        default=(1.0, 0.72, 0.0, 1.0), min=0.0, max=1.0,
        update=_on_imt_appearance_value_update,
    )
    imt_center_line_yellow: BoolProperty(
        name="Opposing center line: yellow", default=False,
        options={"HIDDEN"},
    )
    imt_texture: FloatProperty(name="Texture", default=0.25, min=0.0, max=1.0, update=_on_imt_appearance_value_update)
    imt_cracks_density: FloatProperty(name="Cracks density", default=0.70, min=0.0, max=1.0, update=_on_imt_appearance_value_update)
    imt_cracks_scale: FloatProperty(name="Cracks scale", default=0.40, min=0.0, max=1.0, update=_on_imt_appearance_value_update)
    imt_voids_density: FloatProperty(name="Voids density", default=0.20, min=0.0, max=1.0, update=_on_imt_appearance_value_update)
    imt_voids_scale: FloatProperty(name="Voids scale", default=1.0, min=0.0, max=1.0, update=_on_imt_appearance_value_update)
    imt_crosswalk_width: FloatProperty(name="Zebra width", default=3.0, min=0.1, max=16.0, unit="LENGTH", update=_on_imt_appearance_value_update)
    imt_crosswalk_dash_length: FloatProperty(name="Zebra stripe", default=0.45, min=0.05, max=4.0, unit="LENGTH", update=_on_imt_appearance_value_update)
    imt_crosswalk_gap_length: FloatProperty(name="Zebra gap", default=0.55, min=0.05, max=4.0, unit="LENGTH", update=_on_imt_appearance_value_update)
    imt_crosswalk_offset: FloatProperty(name="Zebra inner offset", default=0.40, min=0.0, max=8.0, unit="LENGTH", update=_on_imt_appearance_value_update)
    imt_stop_line_width: FloatProperty(name="Stop line width", default=0.30, min=0.05, max=2.0, unit="LENGTH", update=_on_imt_appearance_value_update)
    imt_dash_length: FloatProperty(name="Line dash", default=6.0, min=0.05, max=32.0, unit="LENGTH", update=_on_imt_appearance_value_update)
    imt_dash_gap: FloatProperty(name="Line gap", default=10.0, min=0.05, max=32.0, unit="LENGTH", update=_on_imt_appearance_value_update)
    edge_lines: BoolProperty(name="Legacy roadside lines", default=True, options={"HIDDEN"})
    lane_lines: BoolProperty(name="Legacy lane divider lines", default=True, options={"HIDDEN"})
    marking_paint_width: FloatProperty(
        name="Paint width", default=0.15, min=0.05, max=0.5,
        unit="LENGTH", update=_on_geometry_update,
    )
    marking_region_width: FloatProperty(
        name="Texture band width", default=0.4, min=0.2, max=1.0,
        unit="LENGTH", update=_on_geometry_update,
    )
    line_mesh_enabled: BoolProperty(
        name="Generate line mesh", default=False, update=_on_geometry_update,
    )
    node_shoulder_bands: BoolProperty(
        name="Node shoulder bands", default=False, update=_on_geometry_update,
    )
    elevated_height: FloatProperty(name="Elevated height", default=8.0, min=1.0, max=64.0, unit="LENGTH")
    bridge_height: FloatProperty(name="Bridge height", default=12.0, min=1.0, max=128.0, unit="LENGTH")
    deck_depth: FloatProperty(
        name="Elevated deck depth", default=1.0, min=0.1, max=8.0,
        unit="LENGTH", update=_on_geometry_update,
    )
    elevated_edge_mesh: PointerProperty(
        name="Edge mesh (right basis)", type=bpy.types.Object,
        poll=_mesh_object_poll, update=_on_geometry_update,
    )
    elevated_edge_no_split_group: StringProperty(
        name="No-split group", default="CS1_NO_SPLIT", update=_on_geometry_update,
    )
    bridge_deck_depth: FloatProperty(
        name="Bridge deck depth", default=1.5, min=0.1, max=12.0,
        unit="LENGTH", update=_on_geometry_update,
    )
    tunnel_depth: FloatProperty(name="Tunnel depth", default=12.0, min=1.0, max=64.0, unit="LENGTH")
    tunnel_clearance: FloatProperty(
        name="Tunnel clearance", default=5.0, min=2.0, max=16.0,
        unit="LENGTH", update=_on_geometry_update,
    )
    runtime_road_id: StringProperty(name="Runtime road ID", default="example-road")
    runtime_prefab_name: StringProperty(name="Runtime prefab name", default="Road Runtime Example")
    runtime_template_name: StringProperty(name="Template prefab", default="Basic Road")
    runtime_output_dir: StringProperty(
        name="Runtime output", default=DEFAULT_RUNTIME_OUTPUT, subtype="DIR_PATH",
    )
    runtime_auto_export: BoolProperty(name="Auto export every second", default=False)
    runtime_prop_id: StringProperty(name="Runtime Prop ID", default="example-decal")
    runtime_prop_shader: StringProperty(name="Prop shader", default="Custom/Props/Decal/Blend")


class CS1ROAD_UL_roads(UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_propname, index):
        if self.layout_type not in {"DEFAULT", "COMPACT"}:
            layout.label(text=item.road_name)
            return
        road_lanes = sum(lane.zone == "ROAD" for lane in item.lanes)
        pedestrian_lanes = sum(lane.lane_type == "PEDESTRIAN" for lane in item.lanes)
        _, total_width, _ = _cross_section(item)
        median = "M" if item.median_enabled else "-"
        row = layout.row(align=True)
        row.label(text=item.road_name or "Unnamed road", icon="MESH_GRID")
        row.label(text=f"{road_lanes}R {pedestrian_lanes}P {total_width:.1f}m {median}")


def _unique_road_value(scene, attribute: str, requested: str, exclude=None) -> str:
    base = requested.strip() or "Road"
    existing = {
        str(getattr(road, attribute)).strip().lower()
        for road in scene.cs1_roads
        if exclude is None or road.as_pointer() != exclude.as_pointer()
    }
    if base.lower() not in existing:
        return base
    suffix = 2
    while f"{base}-{suffix}".lower() in existing:
        suffix += 1
    return f"{base}-{suffix}"


class CS1ROAD_OT_road_add(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.road_add", "Add road", {"REGISTER", "UNDO"}

    def execute(self, context):
        scene = context.scene
        road = scene.cs1_roads.add()
        number = len(scene.cs1_roads)
        road.road_name = _unique_road_value(scene, "road_name", f"Road {number}", road)
        road.runtime_road_id = _unique_road_value(
            scene, "runtime_road_id", f"road-{number}", road,
        )
        road.runtime_prefab_name = _unique_road_value(
            scene, "runtime_prefab_name", f"Road {number}", road,
        )
        _ensure_default_lanes(road)
        scene.cs1_active_road_index = number - 1
        return {"FINISHED"}


class CS1ROAD_OT_road_duplicate(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.road_duplicate", "Duplicate road", {"REGISTER", "UNDO"}

    def execute(self, context):
        scene = context.scene
        source = active_road(scene)
        target = scene.cs1_roads.add()
        _copy_property_group(source, target)
        target.road_name = _unique_road_value(
            scene, "road_name", f"{source.road_name} Copy", target,
        )
        target.runtime_road_id = _unique_road_value(
            scene, "runtime_road_id",
            f"{safe_road_id(source.runtime_road_id)}-copy", target,
        )
        target.runtime_prefab_name = _unique_road_value(
            scene, "runtime_prefab_name",
            f"{source.runtime_prefab_name} Copy", target,
        )
        target.runtime_auto_export = False
        scene.cs1_active_road_index = len(scene.cs1_roads) - 1
        return {"FINISHED"}


class CS1ROAD_OT_road_remove(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.road_remove", "Remove road", {"REGISTER", "UNDO"}

    @classmethod
    def poll(cls, context):
        return len(context.scene.cs1_roads) > 1

    def invoke(self, context, event):
        return context.window_manager.invoke_confirm(self, event)

    def execute(self, context):
        scene = context.scene
        index = max(0, min(scene.cs1_active_road_index, len(scene.cs1_roads) - 1))
        scene.cs1_roads.remove(index)
        scene.cs1_active_road_index = min(index, len(scene.cs1_roads) - 1)
        return {"FINISHED"}


class CS1ROAD_UL_boundaries(UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_propname, index):
        if self.layout_type in {"DEFAULT", "COMPACT"}:
            row = layout.row(align=True)
            row.prop(item, "marking_enabled", text="")
            row.label(text=item.name or item.boundary_id)
            if item.marking_enabled:
                row.prop(item, "marking_style", text="")
            else:
                row.label(text="No line")
        else:
            layout.label(text=item.name or item.boundary_id)


class CS1ROAD_OT_lane_add(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.lane_add", "Add lane", {"REGISTER", "UNDO"}

    def execute(self, context):
        props = active_road(context.scene)
        lane = props.lanes.add()
        _ensure_lane_ids(props)
        lane.name = f"Lane {len(props.lanes)}"
        props.active_lane_index = len(props.lanes) - 1
        _sync_cross_section_state(props)
        _schedule_live_preview(props, context)
        return {"FINISHED"}


class CS1ROAD_OT_lanes_reset(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.lanes_reset", "Create default lanes", {"REGISTER", "UNDO"}

    def execute(self, context):
        props = active_road(context.scene)
        props.lanes.clear()
        _add_default_lanes(props)
        props.active_lane_index = 0
        _sync_cross_section_state(props)
        _schedule_live_preview(props, context)
        return {"FINISHED"}


class CS1ROAD_OT_lane_remove(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.lane_remove", "Remove lane", {"REGISTER", "UNDO"}
    index: IntProperty(default=-1)

    def execute(self, context):
        props = active_road(context.scene)
        if props.lanes:
            index = self.index if 0 <= self.index < len(props.lanes) else props.active_lane_index
            props.lanes.remove(min(index, len(props.lanes) - 1))
            props.active_lane_index = max(0, min(props.active_lane_index, len(props.lanes) - 1))
            _sync_cross_section_state(props)
            _schedule_live_preview(props, context)
        return {"FINISHED"}


class CS1ROAD_OT_lane_move(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.lane_move", "Move lane", {"REGISTER", "UNDO"}
    direction: EnumProperty(items=(("UP", "Up", ""), ("DOWN", "Down", "")))
    index: IntProperty(default=-1)

    def execute(self, context):
        props = active_road(context.scene)
        source = self.index if 0 <= self.index < len(props.lanes) else props.active_lane_index
        if not 0 <= source < len(props.lanes):
            return {"FINISHED"}
        zone = props.lanes[source].zone
        peers = [index for index, lane in enumerate(props.lanes) if lane.zone == zone]
        peer_index = peers.index(source)
        target_peer = peer_index - 1 if self.direction == "UP" else peer_index + 1
        if 0 <= target_peer < len(peers):
            target = peers[target_peer]
            props.lanes.move(source, target)
            props.active_lane_index = target
            _sync_cross_section_state(props)
            _schedule_live_preview(props, context)
        return {"FINISHED"}


_CROSS_SECTION_COLUMN_WIDTHS = (0.18, 0.28, 0.16, 0.16, 0.10, 0.12)


def _cross_section_table_cells(layout):
    remainder = layout.row(align=True)
    remaining_width = 1.0
    cells = []
    for width in _CROSS_SECTION_COLUMN_WIDTHS[:-1]:
        split = remainder.split(factor=width / remaining_width, align=True)
        cells.append(split.column(align=True))
        remainder = split.column(align=True)
        remaining_width -= width
    cells.append(remainder)
    return cells


def _draw_lane_table_row(layout, props, lane, index: int) -> None:
    cells = _cross_section_table_cells(layout)
    automatic_parking = lane.lane_id in AUTO_PARKING_LANE_IDS
    width_cell = cells[1].row(align=True)
    if automatic_parking:
        cells[0].label(text=lane.name)
        width_cell.label(text=f"{lane.width:.2f} m")
        cells[2].label(text=lane.direction.title())
        cells[3].label(text="Parking")
    else:
        cells[0].prop(lane, "zone", text="")
    if automatic_parking:
        pass
    elif lane.zone in {"LEFT_SIDEWALK", "RIGHT_SIDEWALK"}:
        width_cell.prop(props, "sidewalk_width", text="")
        cells[2].label(text="Both")
        cells[3].label(text="Pedestrian")
    else:
        width_cell.prop(lane, "width", text="")
        cells[2].prop(lane, "direction", text="")
        cells[3].prop(lane, "vehicle_type", text="")
    cells[4].prop(lane, "speed_limit", text="")
    actions = cells[5].row(align=True)
    actions.enabled = not automatic_parking
    up = actions.operator("cs1_road.lane_move", text="", icon="TRIA_UP")
    up.direction, up.index = "UP", index
    down = actions.operator("cs1_road.lane_move", text="", icon="TRIA_DOWN")
    down.direction, down.index = "DOWN", index
    remove = actions.operator("cs1_road.lane_remove", text="", icon="X")
    remove.index = index


def _draw_shoulder_table_row(layout, props, side: str) -> None:
    cells = _cross_section_table_cells(layout)
    cells[0].label(text=f"Shoulder {side}")
    cells[1].label(text=f"{props.shoulder_width:.2f} m")
    cells[2].label(text="-")
    cells[3].label(text="Not lane")
    cells[4].label(text="-")


class CS1ROAD_OT_boundaries_sync(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.boundaries_sync", "Synchronize boundaries", {"REGISTER", "UNDO"}

    def execute(self, context):
        props = active_road(context.scene)
        _sync_boundaries(props)
        _schedule_live_preview(props, context)
        return {"FINISHED"}


def _show_only_mode(active_mode: str) -> None:
    for mode, _, _ in MODE_ITEMS:
        collection = bpy.data.collections.get(f"CS1_ROAD_{mode}")
        if collection is not None:
            collection.hide_viewport = collection.hide_render = mode != active_mode


def _show_all_modes() -> None:
    for mode, _, _ in MODE_ITEMS:
        collection = bpy.data.collections.get(f"CS1_ROAD_{mode}")
        if collection is not None:
            collection.hide_viewport = collection.hide_render = False


class CS1ROAD_OT_build_preview(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.build_preview", "Build active mode", {"REGISTER", "UNDO"}

    def execute(self, context):
        props = active_road(context.scene)
        try:
            build_mode(context.scene, props.mode)
        except ValueError as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        if warning := _cross_section_warning(props):
            self.report({"WARNING"}, warning)
        _show_only_mode(props.mode)
        return {"FINISHED"}


class CS1ROAD_OT_build_all(Operator):
    bl_idname, bl_label, bl_options = "cs1_road.build_all", "Build all modes", {"REGISTER", "UNDO"}

    def execute(self, context):
        props = active_road(context.scene)
        try:
            for mode, _, _ in MODE_ITEMS:
                build_mode(context.scene, mode)
        except ValueError as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        if warning := _cross_section_warning(props):
            self.report({"WARNING"}, warning)
        _show_all_modes()
        return {"FINISHED"}


class CS1ROAD_OT_reload_surface_texture(Operator):
    bl_idname = "cs1_road.reload_surface_texture"
    bl_label = "Reload generated textures"
    bl_description = "Reload surface, structure, and tunnel PNGs exported by Photoshop Generator"

    def execute(self, context):
        try:
            props = active_road(context.scene)
            _material(
                props.marking_paint_width,
                props.marking_region_width,
                props.road_color,
                force_texture_reload=True,
            )
            _structure_material(force_reload=True)
            _tunnel_material(force_reload=True)
        except (OSError, RuntimeError, ValueError) as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        return {"FINISHED"}


def _runtime_lanes(props):
    _sync_lane_derived_values(props)
    return [
        {
            "lane_id": lane.lane_id,
            "position": position,
            "width": lane.width,
            "vertical_offset": lane.vertical_offset,
            "stop_offset": lane.stop_offset,
            "speed_limit": lane.speed_limit,
            "direction": lane.direction,
            "lane_type": lane.lane_type,
            "vehicle_type": lane.vehicle_type,
            "allow_connect": lane.allow_connect,
        }
        for lane, position in _lane_positions(props)
    ]


def _runtime_mode_objects():
    result = {}
    for mode, _, _ in MODE_ITEMS:
        collection = bpy.data.collections.get(f"CS1_ROAD_{mode}")
        if collection is None:
            continue
        by_part = {
            obj.get("cs1_road_part"): obj
            for obj in collection.objects
            if obj.type == "MESH" and obj.get("cs1_road_mode") == mode
        }
        if "segment" in by_part and "node" in by_part:
            result[mode] = [by_part["segment"], by_part["node"]]
    return result


def _runtime_output_path(props) -> Path:
    return Path(bpy.path.abspath(props.runtime_output_dir)).resolve()


def _export_runtime_scene(scene):
    props = active_road(scene)
    _sync_cross_section_state(props)
    _ensure_lane_ids(props)
    _, _, half_width = _cross_section(props)
    modes = _runtime_mode_objects()
    if not modes:
        raise ValueError("Build at least one road mode before runtime export")
    return export_runtime_bundle(
        _runtime_output_path(props),
        safe_road_id(props.runtime_road_id),
        props.runtime_prefab_name.strip() or props.road_name,
        props.runtime_template_name.strip() or "Basic Road",
        half_width,
        props.sidewalk_width,
        props.node_min_corner_offset,
        {
            "white_color": list(props.imt_white_color),
            "yellow_color": list(props.imt_yellow_color),
            "center_line_yellow": props.imt_center_line_yellow,
            "roadside_lines": props.roadside_lines,
            "lane_separator_style": props.lane_separator_style,
            "center_line_style": props.center_line_style,
            "texture": props.imt_texture,
            "cracks": [props.imt_cracks_density, props.imt_cracks_scale],
            "voids": [props.imt_voids_density, props.imt_voids_scale],
            "crosswalk_width": props.imt_crosswalk_width,
            "crosswalk_dash_length": props.imt_crosswalk_dash_length,
            "crosswalk_gap_length": props.imt_crosswalk_gap_length,
            "crosswalk_offset": props.imt_crosswalk_offset,
            "stop_line_width": props.imt_stop_line_width,
            "line_width": props.marking_paint_width,
            "dash_length": props.imt_dash_length,
            "dash_gap": props.imt_dash_gap,
        },
        _runtime_lanes(props),
        modes,
        DEFAULT_TEXTURE_LAYOUT,
        {
            SHARED_SURFACE_MATERIAL: "surface",
            SHARED_STRUCTURE_MATERIAL: "structure",
            SHARED_TUNNEL_MATERIAL: "tunnel",
        },
    )


def _authoring_fingerprint(props):
    lanes = tuple(
        (
            lane.lane_id, lane.zone, lane.width, lane.direction, lane.lane_type,
            lane.vehicle_type, lane.speed_limit,
            lane.stop_offset, lane.allow_connect,
        )
        for lane in props.lanes
    )
    boundaries = tuple(
        (
            item.boundary_id, item.role, item.marking_enabled,
            item.marking_role, item.marking_style,
        )
        for item in props.boundaries
    )
    edge = props.elevated_edge_mesh
    edge_fingerprint = geometry_fingerprint([edge]) if edge is not None else ""
    median = props.median_mesh
    median_fingerprint = geometry_fingerprint([median]) if median is not None else ""
    return repr((
        props.road_name, lanes, boundaries, props.between_sidewalks_width,
        props.shoulder_width, props.roadside_use, props.parking_lane_width,
        props.roadside_lines, props.lane_separator_style,
        props.center_line_style,
        props.sidewalk_width, props.depress_roadway, props.marking_paint_width,
        props.marking_region_width, props.line_mesh_enabled,
        props.node_shoulder_bands,
        props.node_min_corner_offset, tuple(props.road_color),
        tuple(props.imt_white_color), tuple(props.imt_yellow_color),
        props.imt_center_line_yellow,
        props.imt_texture, props.imt_cracks_density, props.imt_cracks_scale,
        props.imt_voids_density, props.imt_voids_scale,
        props.imt_crosswalk_width, props.imt_crosswalk_dash_length,
        props.imt_crosswalk_gap_length, props.imt_crosswalk_offset,
        props.imt_stop_line_width,
        props.imt_dash_length, props.imt_dash_gap,
        props.median_profile, props.median_enabled,
        props.median_width, props.median_height,
        props.median_with_curb, props.median_curb_width,
        props.median_no_split_group, median_fingerprint,
        props.elevated_height, props.bridge_height, props.deck_depth,
        props.bridge_deck_depth, props.tunnel_depth, props.tunnel_clearance,
        props.elevated_edge_no_split_group, edge_fingerprint,
        props.runtime_road_id, props.runtime_prefab_name,
        props.runtime_template_name, props.runtime_output_dir,
    ))


def _runtime_geometry_fingerprint():
    objects = [obj for values in _runtime_mode_objects().values() for obj in values]
    return geometry_fingerprint(objects)


def _runtime_texture_fingerprint():
    return texture_fingerprint(DEFAULT_TEXTURE_LAYOUT)


def _runtime_auto_export_timer():
    for scene in bpy.data.scenes:
        props = active_road(scene)
        if props is None or not props.runtime_auto_export:
            continue
        scene_key = scene.as_pointer()
        current_authoring = _authoring_fingerprint(props)
        previous = _AUTO_EXPORT_STATE.get(scene_key)
        try:
            if previous is None or previous[0] != current_authoring:
                for mode, _, _ in MODE_ITEMS:
                    build_mode(scene, mode)
            current_geometry = _runtime_geometry_fingerprint()
            current_textures = _runtime_texture_fingerprint()
            if previous is not None and previous[2] != current_textures:
                _material(
                    props.marking_paint_width,
                    props.marking_region_width,
                    props.road_color,
                    force_texture_reload=True,
                )
                _structure_material(force_reload=True)
                _tunnel_material(force_reload=True)
            current_state = (
                current_authoring, current_geometry, current_textures,
            )
            if previous is None or previous != current_state:
                _export_runtime_scene(scene)
                _AUTO_EXPORT_STATE[scene_key] = current_state
        except (OSError, ValueError, TypeError) as error:
            signature = f"{type(error).__name__}: {error}"
            if _AUTO_EXPORT_ERRORS.get(scene_key) != signature:
                print(f"CS1 Road Builder runtime export: {signature}")
                _AUTO_EXPORT_ERRORS[scene_key] = signature
        else:
            _AUTO_EXPORT_ERRORS.pop(scene_key, None)
    try:
        _reload_loaded_generator_images()
    except (OSError, RuntimeError, ValueError) as error:
        signature = f"{type(error).__name__}: {error}"
        if _AUTO_EXPORT_ERRORS.get("textures") != signature:
            print(f"CS1 Road Builder texture reload: {signature}")
            _AUTO_EXPORT_ERRORS["textures"] = signature
    else:
        _AUTO_EXPORT_ERRORS.pop("textures", None)
    return 1.0


class CS1ROAD_OT_export_runtime(Operator):
    bl_idname, bl_label = "cs1_road.export_runtime", "Build and export runtime bundle"

    def execute(self, context):
        try:
            for mode, _, _ in MODE_ITEMS:
                build_mode(context.scene, mode)
            payload = _export_runtime_scene(context.scene)
            _AUTO_EXPORT_STATE[context.scene.as_pointer()] = (
                _authoring_fingerprint(active_road(context.scene)),
                _runtime_geometry_fingerprint(),
                _runtime_texture_fingerprint(),
            )
        except (OSError, ValueError, TypeError) as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        self.report({"INFO"}, f"Runtime bundle {payload['revision'][:12]}")
        return {"FINISHED"}


class CS1ROAD_OT_export_runtime_prop(Operator):
    bl_idname, bl_label = "cs1_road.export_runtime_prop", "Export selected Prop/Decal mesh"

    def execute(self, context):
        props = active_road(context.scene)
        obj = context.active_object
        if obj is None or obj.type != "MESH":
            self.report({"ERROR"}, "Select one Mesh object to export")
            return {"CANCELLED"}
        try:
            payload = export_prop_bundle(
                _runtime_output_path(props), props.runtime_prop_id, obj,
                props.runtime_prop_shader,
            )
        except (OSError, ValueError, TypeError) as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        self.report({"INFO"}, f"Prop bundle {payload['revision'][:12]}")
        return {"FINISHED"}


def _load_lane(target, source) -> None:
    target.lane_id = str(source.get("id", source.get("lane_id", "")))
    target.name = str(source.get("name", "Lane"))
    target.zone = _enum_value(str(source.get("zone", "ROAD")), LANE_ZONE_ITEMS, "ROAD")
    target.width = float(source.get("width", target.width))
    target.direction = _enum_value(str(source.get("direction", "FORWARD")), DIRECTION_ITEMS, "FORWARD")
    target.lane_type = _enum_value(str(source.get("lane_type", "VEHICLE")), LANE_TYPE_ITEMS, "VEHICLE")
    vehicle_type = _enum_value(str(source.get("vehicle_type", "CAR")), VEHICLE_TYPE_ITEMS, "CAR")
    if target.zone == "ROAD" and target.lane_type == "VEHICLE" and vehicle_type == "NONE":
        vehicle_type = "CAR"
    target.vehicle_type = vehicle_type
    target.speed_limit = float(source.get("speed_limit", target.speed_limit))
    target.vertical_offset = float(source.get("vertical_offset", target.vertical_offset))
    target.stop_offset = float(source.get("stop_offset", target.stop_offset))
    target.allow_connect = bool(source.get("allow_connect", target.allow_connect))


def _layout_strips(props):
    strips = [{"id": "strip-left-sidewalk", "function": "SIDEWALK", "width": props.sidewalk_width, "surface_style": "SIDEWALK"}]
    if props.shoulder_width > 1e-8:
        strips.append({"id": "strip-left-shoulder", "function": "SHOULDER", "width": props.shoulder_width, "surface_style": "ASPHALT"})
    road_lanes = [item for item in props.lanes if item.zone == "ROAD"]
    split_index = median_split_index(road_lanes) if props.median_enabled else -1
    for index, lane in enumerate(road_lanes):
        if index == split_index:
            strips.append({
                "id": "strip-median", "function": "MEDIAN",
                "width": props.median_width, "surface_style": "MEDIAN",
            })
        strips.append({"id": _strip_id(lane), "function": "CARRIAGEWAY", "width": lane.width, "surface_style": "ASPHALT"})
    if props.shoulder_width > 1e-8:
        strips.append({"id": "strip-right-shoulder", "function": "SHOULDER", "width": props.shoulder_width, "surface_style": "ASPHALT"})
    strips.append({"id": "strip-right-sidewalk", "function": "SIDEWALK", "width": props.sidewalk_width, "surface_style": "SIDEWALK"})
    return strips


def _export_lanes(props):
    _sync_lane_derived_values(props)
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
            "profile_id": (
                "CURB"
                if item.role == "CURB" or (
                    item.role == "MEDIAN_EDGE" and props.median_with_curb
                )
                else "CUSTOM" if item.role == "MEDIAN_EDGE" else "FLAT"
            ),
            "marking": marking,
        })
    total_width = sum(strip["width"] for strip in strips)
    return {"alignment_offset_from_left": total_width * 0.5, "strips": strips, "boundaries": boundaries}


class CS1ROAD_OT_import_spec(Operator, ImportHelper):
    bl_idname, bl_label, filename_ext = "cs1_road.import_spec", "Load road JSON", ".json"
    filter_glob: StringProperty(default="*.json", options={"HIDDEN"})

    def execute(self, context):
        data = json.loads(Path(self.filepath).read_text(encoding="utf-8"))
        props = active_road(context.scene)
        props.road_name = data.get("name", props.road_name)
        schema_version = int(data.get("schema_version", 2))
        surface_style = data.get("styles", {}).get("surface", {})
        road_color = surface_style.get("road_color", DEFAULT_ROAD_COLOR)
        if not isinstance(road_color, (list, tuple)) or len(road_color) != 3:
            self.report({"ERROR"}, "styles.surface.road_color must contain three values")
            return {"CANCELLED"}
        props.road_color = tuple(float(value) for value in road_color)
        cross = data.get("shared_geometry", data.get("cross_section", {}))
        requested_span = cross.get("between_sidewalks_width")
        if requested_span is not None:
            props.between_sidewalks_width = float(requested_span)
        props.roadside_use = _enum_value(
            str(cross.get("roadside_use", props.roadside_use)),
            ROADSIDE_USE_ITEMS, "SHOULDER",
        )
        props.parking_lane_width = float(
            cross.get("parking_lane_width", props.parking_lane_width)
        )
        profile = str(cross.get("surface_profile", "DEPRESSED")).upper()
        props.depress_roadway = bool(cross.get("depress_roadway", profile == "DEPRESSED"))
        props.line_mesh_enabled = bool(
            cross.get("line_mesh_enabled", props.line_mesh_enabled)
        )
        median_present = "median" in cross
        median = cross.get("median", {})
        imported_median_enabled = bool(
            median.get("enabled", props.median_enabled)
        )
        imported_median_width = float(
            median.get("width", props.median_width)
        )
        imported_median_with_curb = bool(
            median.get("with_curb", props.median_with_curb)
        )
        props.median_width = imported_median_width
        props.median_height = float(median.get("height", props.median_height))
        imported_median_profile = str(median.get("profile", "")).upper()
        if not imported_median_profile:
            imported_median_profile = (
                "NONE" if not imported_median_enabled
                else "CURB" if imported_median_with_curb else "MESH"
            )
        props.median_curb_width = float(
            median.get("curb_top_width", props.median_curb_width)
        )
        props.median_no_split_group = str(
            median.get("no_split_group", props.median_no_split_group)
        )
        median_mesh_name = str(median.get("mesh_object", "")).strip()
        props.median_mesh = bpy.data.objects.get(median_mesh_name) if median_mesh_name else None
        legacy_markings = data.get("markings", {})
        imported_shoulder_width = props.shoulder_width
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
            imported_shoulder_width = left_shoulder
            has_median_strip = "strip-median" in strips
            if median_present and imported_median_enabled != has_median_strip:
                self.report(
                    {"ERROR"},
                    "shared_geometry.median enabled state and strip-median disagree",
                )
                return {"CANCELLED"}
            if has_median_strip:
                strip_median_width = float(
                    strips["strip-median"].get("width", imported_median_width)
                )
                if (
                    median_present
                    and abs(strip_median_width - imported_median_width) > 1e-6
                ):
                    self.report(
                        {"ERROR"},
                        "shared_geometry.median width and strip-median width disagree",
                    )
                    return {"CANCELLED"}
                if not median_present:
                    imported_median_enabled = True
                    imported_median_width = strip_median_width
                    props.median_width = strip_median_width
                    imported_median_profile = "CURB"
            elif not median_present:
                imported_median_enabled = False
                imported_median_profile = "NONE"
            marking_styles = data.get("styles", {}).get("markings", {})
            solid = marking_styles.get("SOLID_WHITE", {})
            props.marking_paint_width = solid.get("paint_width", props.marking_paint_width)
            props.marking_region_width = solid.get("region_width", props.marking_region_width)
            rules = marking_styles.get("rules", {})
            props.roadside_lines = bool(
                rules.get("roadside_lines", True)
            )
            props.lane_separator_style = _enum_value(
                str(rules.get("lane_separator_style", "DASHED_WHITE")),
                LANE_SEPARATOR_STYLE_ITEMS, "DASHED_WHITE",
            )
            props.center_line_style = _enum_value(
                str(rules.get("center_line_style", "DASHED_WHITE")),
                CENTER_LINE_STYLE_ITEMS, "DASHED_WHITE",
            )
            imt = data.get("styles", {}).get("imt_preview", {})
            _load_imt_appearance(props, imt)
        else:
            props.shoulder_width = cross.get("shoulder_width", props.shoulder_width)
            imported_shoulder_width = props.shoulder_width
            props.sidewalk_width = cross.get("sidewalk_width", props.sidewalk_width)
            props.edge_lines = legacy_markings.get("edge_lines", props.edge_lines)
            props.lane_lines = legacy_markings.get("lane_lines", props.lane_lines)
            props.marking_paint_width = legacy_markings.get("paint_width", props.marking_paint_width)
            props.marking_region_width = legacy_markings.get("region_width", props.marking_region_width)
        node = data.get("node", {})
        props.node_shoulder_bands = node.get("shoulder_bands", props.node_shoulder_bands)
        props.node_min_corner_offset = float(node["min_corner_offset"])
        props.lanes.clear()
        for source in data.get("lanes", []):
            lane_source = dict(source)
            if schema_version >= 3:
                strip = strips.get(str(source.get("surface_strip_id", "")), {})
                lane_source["width"] = float(source.get("lateral_end", 0.0)) - float(source.get("lateral_start", 0.0))
                if lane_source["width"] <= 0.0:
                    lane_source["width"] = strip.get("width", 3.0)
            _load_lane(props.lanes.add(), lane_source)
        props.median_profile = _enum_value(
            imported_median_profile, MEDIAN_PROFILE_ITEMS, "NONE",
        )
        if requested_span is None:
            imported_road_width = sum(
                lane.width for lane in props.lanes if lane.zone == "ROAD"
            )
            props.between_sidewalks_width = (
                imported_road_width
                + 2.0 * imported_shoulder_width
                + (imported_median_width if imported_median_enabled else 0.0)
            )
        _ensure_default_lanes(props)
        _sync_lane_derived_values(props)
        if schema_version >= 3:
            _sync_boundaries(props)
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
        props = active_road(context.scene)
        _sync_cross_section_state(props)
        _ensure_lane_ids(props)
        _sync_boundaries(props)
        data = {
            "schema_version": 3, "name": props.road_name,
            "shared_geometry": {
                "segment_length": MODE_LENGTH, "node_length": MODE_LENGTH,
                "segment_slices": SEGMENT_SLICES, "node_slices": NODE_SLICES,
                "curb_height": ROADWAY_DEPRESSION,
                "surface_profile": "DEPRESSED" if props.depress_roadway else "FLUSH",
                "line_mesh_enabled": props.line_mesh_enabled,
                "between_sidewalks_width": props.between_sidewalks_width,
                "roadside_use": props.roadside_use,
                "parking_lane_width": props.parking_lane_width,
                "median": {
                    "enabled": props.median_enabled,
                    "profile": props.median_profile,
                    "width": props.median_width,
                    "height": props.median_height,
                    "with_curb": props.median_with_curb,
                    "curb_top_width": props.median_curb_width,
                    "mesh_object": props.median_mesh.name if props.median_mesh else None,
                    "no_split_group": props.median_no_split_group,
                },
            },
            "styles": {
                "surface": {
                    "road_color": list(props.road_color),
                },
                "markings": {"SOLID_WHITE": {
                    "paint_width": props.marking_paint_width,
                    "region_width": props.marking_region_width,
                    "texture_tile": "solid_white",
                }, "rules": {
                    "roadside_lines": props.roadside_lines,
                    "lane_separator_style": props.lane_separator_style,
                    "center_line_style": props.center_line_style,
                }},
                "imt_preview": {
                    "preset": props.imt_appearance_preset,
                    "white_color": list(props.imt_white_color),
                    "yellow_color": list(props.imt_yellow_color),
                    "center_line_yellow": props.imt_center_line_yellow,
                    "roadside_lines": props.roadside_lines,
                    "lane_separator_style": props.lane_separator_style,
                    "center_line_style": props.center_line_style,
                    "texture": props.imt_texture,
                    "cracks": [props.imt_cracks_density, props.imt_cracks_scale],
                    "voids": [props.imt_voids_density, props.imt_voids_scale],
                    "crosswalk_width": props.imt_crosswalk_width,
                    "crosswalk_dash_length": props.imt_crosswalk_dash_length,
                    "crosswalk_gap_length": props.imt_crosswalk_gap_length,
                    "crosswalk_offset": props.imt_crosswalk_offset,
                    "stop_line_width": props.imt_stop_line_width,
                    "line_width": props.marking_paint_width,
                    "dash_length": props.imt_dash_length,
                    "dash_gap": props.imt_dash_gap,
                },
            },
            "layout": _export_layout(props),
            "node": {
                "length": MODE_LENGTH,
                "center_split": True,
                "shoulder_bands": props.node_shoulder_bands,
                "min_corner_offset": props.node_min_corner_offset,
                "transition_target": None if props.depress_roadway else "DEPRESSED",
            },
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


class CS1ROAD_OT_import_spec_new(Operator, ImportHelper):
    bl_idname, bl_label, filename_ext = "cs1_road.import_spec_new", "Import road as new", ".json"
    filter_glob: StringProperty(default="*.json", options={"HIDDEN"})

    def execute(self, context):
        scene = context.scene
        previous_index = scene.cs1_active_road_index
        scene.cs1_roads.add()
        scene.cs1_active_road_index = len(scene.cs1_roads) - 1
        result = bpy.ops.cs1_road.import_spec(filepath=self.filepath)
        if result != {"FINISHED"}:
            scene.cs1_roads.remove(len(scene.cs1_roads) - 1)
            scene.cs1_active_road_index = previous_index
            return {"CANCELLED"}
        road = active_road(scene)
        if not road.runtime_road_id or road.runtime_road_id == "example-road":
            road.runtime_road_id = _unique_road_value(
                scene, "runtime_road_id",
                safe_road_id(Path(self.filepath).stem), road,
            )
        if not road.runtime_prefab_name or road.runtime_prefab_name == "Road Runtime Example":
            road.runtime_prefab_name = _unique_road_value(
                scene, "runtime_prefab_name", road.road_name, road,
            )
        return {"FINISHED"}


class CS1ROAD_OT_export_all_specs(Operator):
    bl_idname, bl_label = "cs1_road.export_all_specs", "Export all roads"
    directory: StringProperty(name="Directory", subtype="DIR_PATH")

    def invoke(self, context, event):
        self.directory = str(Path(__file__).resolve().parents[2] / "specs")
        context.window_manager.fileselect_add(self)
        return {"RUNNING_MODAL"}

    def execute(self, context):
        scene = context.scene
        output = Path(bpy.path.abspath(self.directory)).resolve()
        output.mkdir(parents=True, exist_ok=True)
        previous_index = scene.cs1_active_road_index
        try:
            for index, road in enumerate(scene.cs1_roads):
                scene.cs1_active_road_index = index
                filename = f"{safe_road_id(road.runtime_road_id or road.road_name)}.json"
                result = bpy.ops.cs1_road.export_spec(filepath=str(output / filename))
                if result != {"FINISHED"}:
                    self.report({"ERROR"}, f"Failed to export {road.road_name}")
                    return {"CANCELLED"}
        finally:
            scene.cs1_active_road_index = min(previous_index, len(scene.cs1_roads) - 1)
        self.report({"INFO"}, f"Exported {len(scene.cs1_roads)} roads")
        return {"FINISHED"}


class CS1ROAD_PT_main(Panel):
    bl_label, bl_idname = "CS1 Road Builder", "CS1ROAD_PT_main"
    bl_space_type, bl_region_type, bl_category = "VIEW_3D", "UI", "Road"

    def draw(self, context):
        layout, props = self.layout, active_road(context.scene)
        library = layout.row()
        library.template_list(
            "CS1ROAD_UL_roads", "",
            context.scene, "cs1_roads",
            context.scene, "cs1_active_road_index",
            rows=4,
        )
        buttons = library.column(align=True)
        buttons.operator("cs1_road.road_add", text="", icon="ADD")
        buttons.operator("cs1_road.road_duplicate", text="", icon="DUPLICATE")
        buttons.operator("cs1_road.road_remove", text="", icon="REMOVE")
        summary = layout.box()
        road_lanes = sum(lane.zone == "ROAD" for lane in props.lanes)
        pedestrian_lanes = sum(lane.lane_type == "PEDESTRIAN" for lane in props.lanes)
        _, total_width, _ = _cross_section(props)
        summary.label(text=props.runtime_road_id or "No runtime road ID", icon="KEYTYPE_KEYFRAME_VEC")
        summary.label(text=(
            f"{road_lanes} roadway / {pedestrian_lanes} pedestrian lanes | "
            f"{total_width:.2f} m total"
        ))
        summary.label(text=(
            ("Median" if props.median_enabled else "No median")
            + (" | depressed roadway" if props.depress_roadway else " | flush roadway")
        ))
        layout.prop(props, "road_name")
        tabs = layout.row(align=True)
        tabs.prop(props, "mode", expand=True)
        actions = layout.row(align=True)
        actions.operator("cs1_road.build_preview", text="Build selected", icon="MOD_BUILD")
        actions.operator("cs1_road.build_all", text="Build all", icon="OUTLINER_COLLECTION")


class _CS1RoadChildPanel:
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Road"
    bl_parent_id = "CS1ROAD_PT_main"


class CS1ROAD_PT_shared(_CS1RoadChildPanel, Panel):
    bl_label = "Shared settings"
    bl_idname = "CS1ROAD_PT_shared"

    def draw(self, context):
        layout, props = self.layout, active_road(context.scene)
        layout.prop(props, "depress_roadway")
        layout.prop(props, "node_min_corner_offset")
        layout.prop(props, "road_color")
        row = layout.row(align=True)
        row.prop(props, "marking_paint_width")
        row.prop(props, "marking_region_width")
        if props.marking_region_width <= props.marking_paint_width:
            layout.label(text="Texture band must include asphalt margin.", icon="ERROR")


class CS1ROAD_PT_cross_section(_CS1RoadChildPanel, Panel):
    bl_label = "Cross-section"
    bl_idname = "CS1ROAD_PT_cross_section"

    def draw(self, context):
        box, props = self.layout, active_road(context.scene)
        allocation = _cross_section_allocation(props)
        primary = box.box()
        primary.label(text="Width budget")
        primary.prop(props, "between_sidewalks_width")
        primary.prop(props, "roadside_use")
        if props.roadside_use == "PARKING":
            primary.prop(props, "parking_lane_width")
        primary.prop(props, "median_profile")
        warning = _cross_section_warning(props)
        if warning:
            primary.label(text=warning, icon="INFO")
        elif props.roadside_use == "PARKING":
            primary.label(text="Symmetric parking lanes fit and are generated", icon="CHECKMARK")
        if not props.lanes:
            box.label(text="No lanes. Initialize the road definition.", icon="INFO")
            box.operator("cs1_road.lanes_reset", icon="FILE_REFRESH")
        header = _cross_section_table_cells(box)
        header[0].label(text="Element / lane")
        header[1].label(text="Width")
        header[2].label(text="Direction / height")
        header[3].label(text="Traffic / curb")
        header[4].label(text="Speed")
        indexed_lanes = list(enumerate(props.lanes))
        left_lanes = [(index, lane) for index, lane in indexed_lanes if lane.zone == "LEFT_SIDEWALK"]
        road_lanes = [(index, lane) for index, lane in indexed_lanes if lane.zone == "ROAD"]
        right_lanes = [(index, lane) for index, lane in indexed_lanes if lane.zone == "RIGHT_SIDEWALK"]
        for index, lane in left_lanes:
            _draw_lane_table_row(box, props, lane, index)
        if not left_lanes:
            empty_left = _cross_section_table_cells(box)
            empty_left[0].label(text="Sidewalk L")
            empty_left[1].prop(props, "sidewalk_width", text="")
            empty_left[3].label(text="Not lane")
        _draw_shoulder_table_row(box, props, "L")
        try:
            median_after = median_split_index([lane for _, lane in road_lanes])
        except ValueError:
            median_after = -1
        for road_index, (index, lane) in enumerate(road_lanes, 1):
            _draw_lane_table_row(box, props, lane, index)
            if road_index == median_after:
                median_row = _cross_section_table_cells(box)
                median_row[0].label(text="Median")
                width_cell = median_row[1]
                width_cell.prop(props, "median_width", text="")
                height_cell = median_row[2]
                height_cell.prop(props, "median_height", text="")
                type_cell = median_row[3]
                type_cell.label(text="Curb" if props.median_with_curb else "Mesh")
                median_row[4].label(
                    text="Generated" if props.median_with_curb else "Mesh"
                )
        _draw_shoulder_table_row(box, props, "R")
        for index, lane in right_lanes:
            _draw_lane_table_row(box, props, lane, index)
        if not right_lanes:
            empty_right = _cross_section_table_cells(box)
            empty_right[0].label(text="Sidewalk R")
            empty_right[1].prop(props, "sidewalk_width", text="")
            empty_right[3].label(text="Not lane")
        box.operator("cs1_road.lane_add", text="Add network lane", icon="ADD")
        box.label(text=f"Pedestrian lane fits inside sidewalk (-{SIDEWALK_LANE_TOTAL_INSET:.2f} m)")
        if props.median_enabled:
            median_box = box.box()
            if props.median_with_curb:
                median_box.prop(props, "median_curb_width")
                median_box.label(text="Curb tops and centre top use separate faces")
            else:
                median_box.prop(props, "median_mesh")
                median_box.prop(props, "median_no_split_group")
                median_box.label(text="Mesh is fitted to the configured width and height")
                median_box.label(text="Local Y must span -32..32 m")
        counts = {key: sum(lane.direction == key for lane in props.lanes) for key in ("FORWARD", "BACKWARD", "BOTH")}
        roadway_width, total_width, _ = _cross_section(props)
        box.label(text=(
            f"{len(props.lanes)} lanes (F{counts['FORWARD']} / B{counts['BACKWARD']} / P{counts['BOTH']}) | "
            f"{roadway_width:.2f} m road / {total_width:.2f} m total | "
            f"target {allocation.target_width:.2f} m | 64 m / 20 slices"
        ))


class CS1ROAD_PT_markings(_CS1RoadChildPanel, Panel):
    bl_label = "Road lines"
    bl_idname = "CS1ROAD_PT_markings"
    bl_options = {"DEFAULT_CLOSED"}

    def draw(self, context):
        layout, props = self.layout, active_road(context.scene)
        rules = layout.box()
        rules.label(text="Default lines for this road")
        rules.prop(props, "roadside_lines")
        rules.prop(props, "lane_separator_style")
        rules.prop(props, "center_line_style")
        layout.prop(props, "line_mesh_enabled")
        layout.label(text="Applied to every matching boundary; no per-line editing.")
        if props.center_line_style == "SOLID_YELLOW" and props.line_mesh_enabled:
            layout.label(text="Blender uses the white paint mask; IMT applies yellow.", icon="INFO")
        preview = layout.box()
        preview.label(text="IMT preview: shared appearance")
        preview.prop(props, "imt_appearance_preset")
        colors = preview.row(align=True)
        colors.prop(props, "imt_white_color")
        colors.prop(props, "imt_yellow_color")
        preview.prop(props, "imt_texture")
        cracks = preview.row(align=True)
        cracks.prop(props, "imt_cracks_density")
        cracks.prop(props, "imt_cracks_scale")
        voids = preview.row(align=True)
        voids.prop(props, "imt_voids_density")
        voids.prop(props, "imt_voids_scale")
        zebra = preview.column(align=True)
        zebra.prop(props, "imt_crosswalk_width")
        row = zebra.row(align=True)
        row.prop(props, "imt_crosswalk_dash_length")
        row.prop(props, "imt_crosswalk_gap_length")
        zebra.prop(props, "imt_crosswalk_offset")
        zebra.prop(props, "imt_stop_line_width")
        line_dash = preview.row(align=True)
        line_dash.prop(props, "imt_dash_length")
        line_dash.prop(props, "imt_dash_gap")
        preview.label(text="Zebra outer extension is not exposed by IMT API", icon="INFO")


class CS1ROAD_PT_mode(_CS1RoadChildPanel, Panel):
    bl_label = "Selected mode"
    bl_idname = "CS1ROAD_PT_mode"
    bl_options = {"DEFAULT_CLOSED"}

    def draw_header(self, context):
        labels = {key: label for key, label, _ in MODE_ITEMS}
        self.layout.label(text=labels.get(active_road(context.scene).mode, ""))

    def draw(self, context):
        mode_box, props = self.layout, active_road(context.scene)
        if props.mode == "elevated":
            mode_box.prop(props, "elevated_height"); mode_box.prop(props, "deck_depth")
            mode_box.prop(props, "elevated_edge_mesh")
            mode_box.prop(props, "elevated_edge_no_split_group")
            if props.elevated_edge_mesh:
                mode_box.label(text="Origin X/Z: road surface outer corner")
                mode_box.label(text="Right side basis; left side is X-mirrored")
                mode_box.label(text="Local Y: -32..32 m; negative Z follows deck depth")
                mode_box.label(text="Faces touching no-split vertices stay unsliced")
            _, total_width, _ = _cross_section(props)
            girders = plan_main_girders(total_width)
            mode_box.label(text=f"JIS PC compo: {girders.count} girders / {girders.spacing:.1f} m centers")
            mode_box.label(text=f"35 m reference: 0.70 x {girders.depth:.2f} m rectangle")
            mode_box.label(text=f"Edge offset: {girders.outer_offset:.2f} m")
        elif props.mode == "bridge":
            mode_box.prop(props, "bridge_height"); mode_box.prop(props, "bridge_deck_depth")
            _, total_width, _ = _cross_section(props)
            girders = plan_main_girders(total_width)
            mode_box.label(text=f"JIS PC compo: {girders.count} girders / {girders.spacing:.1f} m centers")
            mode_box.label(text=f"35 m reference: 0.70 x {girders.depth:.2f} m rectangle")
            mode_box.label(text=f"Edge offset: {girders.outer_offset:.2f} m")
        elif props.mode in {"slope", "tunnel"}:
            mode_box.prop(props, "tunnel_depth")
            if props.mode == "tunnel":
                mode_box.prop(props, "tunnel_clearance")
        else:
            mode_box.label(text="No mode-specific settings.")


class CS1ROAD_PT_files(_CS1RoadChildPanel, Panel):
    bl_label = "Road definition files"
    bl_idname = "CS1ROAD_PT_files"
    bl_options = {"DEFAULT_CLOSED"}

    def draw(self, context):
        layout = self.layout
        row = layout.row(align=True)
        row.operator("cs1_road.import_spec_new", text="Import as new", icon="IMPORT")
        row.operator("cs1_road.import_spec", text="Replace active", icon="FILE_REFRESH")
        row = layout.row(align=True)
        row.operator("cs1_road.export_spec", text="Export active", icon="EXPORT")
        row.operator("cs1_road.export_all_specs", text="Export all", icon="EXPORT")


class CS1ROAD_PT_runtime(_CS1RoadChildPanel, Panel):
    bl_label = "Runtime preview"
    bl_idname = "CS1ROAD_PT_runtime"
    bl_options = {"DEFAULT_CLOSED"}

    def draw(self, context):
        runtime, props = self.layout, active_road(context.scene)
        runtime.prop(props, "runtime_road_id")
        runtime.prop(props, "runtime_prefab_name")
        runtime.prop(props, "runtime_template_name")
        runtime.prop(props, "runtime_output_dir")
        runtime.prop(props, "runtime_auto_export")
        runtime.operator("cs1_road.export_runtime", icon="EXPORT")
        runtime.separator()
        runtime.prop(props, "runtime_prop_id")
        runtime.prop(props, "runtime_prop_shader")
        runtime.operator("cs1_road.export_runtime_prop", icon="MESH_PLANE")


class CS1ROAD_PT_development(_CS1RoadChildPanel, Panel):
    bl_label = "Development"
    bl_idname = "CS1ROAD_PT_development"
    bl_options = {"DEFAULT_CLOSED"}

    def draw(self, context):
        development = self.layout
        development.operator(
            "cs1_road.reload_surface_texture",
            text="Reload Photoshop Generator PNGs",
            icon="TEXTURE",
        )
        development.label(text="Uses road-assets/*_d plus optional _a/_p/_r/_n/_s PNGs.")
        development.operator("script.reload", text="Reload Scripts", icon="FILE_REFRESH")
        development.label(text="Rebuild generated meshes after reloading.", icon="INFO")
        development.label(text="Generated objects remain editable meshes.", icon="EDITMODE_HLT")


CLASSES = (
    CS1RoadLane, CS1RoadBoundary, CS1RoadBuilderProperties,
    CS1ROAD_UL_roads, CS1ROAD_UL_boundaries,
    CS1ROAD_OT_road_add, CS1ROAD_OT_road_duplicate, CS1ROAD_OT_road_remove,
    CS1ROAD_OT_lane_add, CS1ROAD_OT_lanes_reset, CS1ROAD_OT_lane_remove, CS1ROAD_OT_lane_move,
    CS1ROAD_OT_boundaries_sync,
    CS1ROAD_OT_build_preview, CS1ROAD_OT_build_all, CS1ROAD_OT_reload_surface_texture,
    CS1ROAD_OT_import_spec, CS1ROAD_OT_export_spec, CS1ROAD_OT_export_runtime,
    CS1ROAD_OT_import_spec_new, CS1ROAD_OT_export_all_specs,
    CS1ROAD_OT_export_runtime_prop,
    CS1ROAD_PT_main, CS1ROAD_PT_shared, CS1ROAD_PT_cross_section,
    CS1ROAD_PT_markings, CS1ROAD_PT_mode, CS1ROAD_PT_files,
    CS1ROAD_PT_runtime, CS1ROAD_PT_development,
)


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)
    bpy.types.Scene.cs1_road_builder = PointerProperty(type=CS1RoadBuilderProperties)
    bpy.types.Scene.cs1_roads = CollectionProperty(type=CS1RoadBuilderProperties)
    bpy.types.Scene.cs1_active_road_index = IntProperty(default=0, min=0)
    if not bpy.app.timers.is_registered(_initialize_scene_lanes):
        bpy.app.timers.register(_initialize_scene_lanes, first_interval=0.0)
    if not bpy.app.timers.is_registered(_runtime_auto_export_timer):
        bpy.app.timers.register(_runtime_auto_export_timer, first_interval=1.0, persistent=True)


def unregister():
    if bpy.app.timers.is_registered(_initialize_scene_lanes):
        bpy.app.timers.unregister(_initialize_scene_lanes)
    if bpy.app.timers.is_registered(_runtime_auto_export_timer):
        bpy.app.timers.unregister(_runtime_auto_export_timer)
    if bpy.app.timers.is_registered(_live_preview_timer):
        bpy.app.timers.unregister(_live_preview_timer)
    _AUTO_EXPORT_STATE.clear()
    _AUTO_EXPORT_ERRORS.clear()
    _LIVE_PREVIEW_PENDING.clear()
    del bpy.types.Scene.cs1_active_road_index
    del bpy.types.Scene.cs1_roads
    del bpy.types.Scene.cs1_road_builder
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)
