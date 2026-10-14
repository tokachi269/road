"""Blender-independent derivation used by the current road prototype.

This isolates behavior that already existed in the Blender add-on. It does not
declare the current persisted schema to be the final product model.
"""

from __future__ import annotations

from copy import deepcopy
from hashlib import sha256
from itertools import product
import json
from math import isfinite
from typing import Any, Iterable, Mapping, NamedTuple, Protocol


MODE_LENGTH = 64.0
SEGMENT_SLICES = 20
NODE_SLICES = 8


def longitudinal_slice_count(value):
    """Explicit mesh tessellation input, bounded to avoid accidental huge builds."""
    if isinstance(value, bool) or not isinstance(value, int) or not 1 <= value <= 128:
        raise ValueError("Longitudinal subdivisions must be an integer from 1 to 128")
    return value
ROADWAY_DEPRESSION = 0.30
SIDEWALK_LANE_TOTAL_INSET = 0.50
MEDIAN_END_OVERHANG = 0.002
MEDIAN_Z_FIGHT_EPSILON = 0.002
PARKING_LANE_DEFAULT_WIDTH = 2.0
AUTHORING_SCHEMA_VERSION = 4
PROFILE_SCHEMA_VERSION = 1
DEFAULT_PROFILE_ID = "urban.default"
LANE_SETTING_FIELDS = ("speed_limit", "stop_offset", "allow_connect")
LANE_METADATA_DEFAULTS = {
    ("VEHICLE", "CAR"): {
        "speed_limit": 1.0,
        "stop_offset": 0.0,
        "allow_connect": True,
    },
    ("PEDESTRIAN", "NONE"): {
        "speed_limit": 0.1,
        "stop_offset": 0.0,
        "allow_connect": True,
    },
}
# Compatibility view used by the existing schema-v1 profile adapter. Values
# remain owned by the exact metadata tuples above.
GLOBAL_LANE_DEFAULTS = {
    "road_speed_limit": LANE_METADATA_DEFAULTS[("VEHICLE", "CAR")]["speed_limit"],
    "pedestrian_speed_limit": LANE_METADATA_DEFAULTS[("PEDESTRIAN", "NONE")]["speed_limit"],
    "stop_offset": LANE_METADATA_DEFAULTS[("VEHICLE", "CAR")]["stop_offset"],
    "allow_connect": LANE_METADATA_DEFAULTS[("VEHICLE", "CAR")]["allow_connect"],
}
STANDARD_VEHICLE_LANE_WIDTH = 3.0
STANDARD_SHOULDER_WIDTH = 0.5

# Placement-section prototype. Existing authoring roads still use their current
# inputs; adapters must explicitly opt into this resolver. Metres are owned here,
# never re-derived by a Runtime UI or a mesh consumer.
PLACEMENT_WIDTHS = (16.0, 32.0, 48.0, 64.0)
PLACEMENT_MIN_SIDEWALK = 2.5
_PLACEMENT_WIDTH = {"NARROW": 16.0, "STANDARD": 32.0, "WIDE": 48.0}
_PLACEMENT_MEDIAN = {"NONE": 0.0, "STANDARD": 1.0, "WIDE": 2.0}
_PLACEMENT_ROADSIDE = {"NONE": 0.0, "STANDARD": STANDARD_SHOULDER_WIDTH, "WIDE": 2.0}
_PLACEMENT_DIVIDER = {
    "CENTERED": 0.0,
    "OFFSET_LEFT": -STANDARD_VEHICLE_LANE_WIDTH * 0.5,
    "OFFSET_RIGHT": STANDARD_VEHICLE_LANE_WIDTH * 0.5,
}
_PLACEMENT_LEVEL = {"FLUSH": 0.0, "DEPRESSED": -ROADWAY_DEPRESSION}


class RoadPlacementOptions(NamedTuple):
    width: str = "NARROW"
    divider: str = "CENTERED"
    median: str = "NONE"
    roadside: str = "STANDARD"
    level: str = "DEPRESSED"


def placement_option_matrix(lanes: Iterable[LaneLike]):
    """Compile the finite menu with the same owner as the physical section.

    One-way roads have no divider/median choice; invalid topology is rejected,
    not hidden by catching resolver errors or mapping to another road type.
    This creates section data only, never mesh objects or game prefabs.
    """
    source = tuple(lanes)
    baseline = resolve_placement_section(source, RoadPlacementOptions())
    opposing = baseline.traffic_boundary is not None
    values = (
        tuple(_PLACEMENT_WIDTH),
        tuple(_PLACEMENT_DIVIDER) if opposing else ("CENTERED",),
        tuple(_PLACEMENT_MEDIAN) if opposing else ("NONE",),
        tuple(_PLACEMENT_ROADSIDE),
        tuple(_PLACEMENT_LEVEL),
    )
    return tuple((option, resolve_placement_section(source, option))
                 for option in (RoadPlacementOptions(*parts) for parts in product(*values)))


def placement_median_uses_mesh(base_profile: str) -> bool:
    """An absent base median uses generated curbs when enabled at placement.

    Only an explicitly mesh-authored median delegates shape to its mesh source.
    The legacy hidden median_with_curb switch may be false for NONE; it is not
    a choice to invent a missing mesh when a placement option enables a median.
    """
    if base_profile not in {"NONE", "CURB", "MESH"}:
        raise ValueError(f"Unknown Base Road median profile: {base_profile}")
    return base_profile == "MESH"


class PlacementLane(NamedTuple):
    lane_id: str
    direction: str
    width: float
    position: float


class PlacementBoundary(NamedTuple):
    left_lane_id: str
    right_lane_id: str
    position: float
    role: str


class PlacementLaneGeometry(NamedTuple):
    lane_id: str
    position: float
    width: float
    vertical_offset: float


class RoadPlacementSection(NamedTuple):
    total_width: float
    curb_left: float
    curb_right: float
    sidewalk_left: float
    sidewalk_right: float
    roadside_left: float
    roadside_right: float
    traffic_boundary: float | None
    median_left: float | None
    median_right: float | None
    roadway_z: float
    lanes: tuple[PlacementLane, ...]
    boundaries: tuple[PlacementBoundary, ...]

    @property
    def center_line_position(self) -> float | None:
        return self.traffic_boundary if self.median_left is None else None

    @property
    def node_reference_pavement_width(self) -> float:
        """Single native shader reference; not either physical sidewalk.

        Keep mirror-equivalent sections on the same neutral reference. Their
        actual asymmetric curbs and lane positions remain independently authored.
        Mixed-section node deformation still requires its own adapter/proof.
        """
        return (self.sidewalk_left + self.sidewalk_right) * 0.5

    def variant_identity(self, base_road_id: str) -> str:
        """Deterministic geometry identity; NOT proof of map-load registration.

        Resolved widths, rather than UI choices, permit cache reuse when two
        requests expand to the same section. Marking appearance is absent.
        """
        if not isinstance(base_road_id, str) or not base_road_id.strip():
            raise ValueError("A stable Base Road ID is required")
        state = {"version": 1, "base_road_id": base_road_id, "section": self._asdict()}
        payload = json.dumps(state, sort_keys=True, separators=(",", ":"), allow_nan=False)
        return "rr-section-v1-" + sha256(payload.encode("utf-8")).hexdigest()


class RoadHeightTransition(NamedTuple):
    start: RoadPlacementSection
    end: RoadPlacementSection

    def road_height(self, fraction: float) -> float:
        if isinstance(fraction, bool) or not isfinite(fraction) or not 0.0 <= fraction <= 1.0:
            raise ValueError("Transition fraction must be finite and within 0..1")
        return self.start.roadway_z + (self.end.roadway_z - self.start.roadway_z) * fraction

    def variant_identity(self, base_road_id: str) -> str:
        if not isinstance(base_road_id, str) or not base_road_id.strip():
            raise ValueError("A stable Base Road ID is required")
        state = {"version": 1, "base_road_id": base_road_id,
                 "start": self.start._asdict(), "end": self.end._asdict()}
        payload = json.dumps(state, sort_keys=True, separators=(",", ":"), allow_nan=False)
        return "rr-height-v1-" + sha256(payload.encode("utf-8")).hexdigest()


def resolve_height_transition(start: RoadPlacementSection, end: RoadPlacementSection) -> RoadHeightTransition:
    """A temporary height-only segment, not an extra normal road level."""
    if not isinstance(start, RoadPlacementSection) or not isinstance(end, RoadPlacementSection):
        raise TypeError("Height transition requires two resolved sections")
    if start.roadway_z not in (0.0, -ROADWAY_DEPRESSION) or end.roadway_z not in (0.0, -ROADWAY_DEPRESSION):
        raise ValueError("Height transition supports only Flush and Depressed")
    if start.roadway_z == end.roadway_z:
        raise ValueError("No height transition is needed for equal endpoint levels")
    if start._replace(roadway_z=end.roadway_z) != end:
        raise ValueError("Height-only transition requires identical horizontal sections and lane topology")
    return RoadHeightTransition(start, end)


def height_transition_lane_endpoints(transition: RoadHeightTransition, source_lanes: Iterable[LaneLike]):
    resolve_height_transition(transition.start, transition.end)
    source = tuple(source_lanes)
    return tuple(zip(placement_lane_geometry(transition.start, source),
                     placement_lane_geometry(transition.end, source)))


def resolve_placement_section(
    lanes: Iterable[LaneLike], options: RoadPlacementOptions,
) -> RoadPlacementSection:
    """Resolve a standard vehicle section without shrinking any input lane.

    The traffic boundary is a physical datum, not network center or a paint
    offset. Surplus network width belongs to each outer sidewalk, not hidden
    roadside strips. This does not generate meshes or mutate NetInfo.
    """
    try:
        requested_width = _PLACEMENT_WIDTH[options.width]
        median = _PLACEMENT_MEDIAN[options.median]
        roadside = _PLACEMENT_ROADSIDE[options.roadside]
        center = _PLACEMENT_DIVIDER[options.divider]
        height = _PLACEMENT_LEVEL[options.level]
    except KeyError as error:
        raise ValueError(f"Unknown placement option: {error.args[0]}") from error
    roadway = tuple(lane for lane in lanes if lane.zone == "ROAD")
    if not roadway:
        raise ValueError("Placement sections require roadway lanes")
    if len({lane.lane_id for lane in roadway}) != len(roadway):
        raise ValueError("Placement lane IDs must be unique")
    for lane in roadway:
        if not lane.lane_id or not isfinite(lane.width) or lane.width <= 0.0:
            raise ValueError("Placement lanes require an ID and a finite positive width")
        if lane.direction not in {"FORWARD", "BACKWARD"}:
            raise ValueError("Only exact forward/backward vehicle topology is supported")
    splits = [i for i in range(1, len(roadway))
              if roadway[i - 1].direction != roadway[i].direction]
    if len(splits) > 1:
        raise ValueError("Interleaved directions require a separate traffic topology")
    total_lane_width = sum(lane.width for lane in roadway)
    split = splits[0] if splits else None
    if split is None:
        if median != 0.0 or center != 0.0:
            raise ValueError("One-way roads have no opposing divider or median")
        cursor = -total_lane_width * 0.5
        traffic_boundary = None
        median_left = median_right = None
    else:
        cursor = center - median * 0.5 - sum(lane.width for lane in roadway[:split])
        traffic_boundary = center
        median_left = center - median * 0.5 if median else None
        median_right = center + median * 0.5 if median else None
    lane_left = cursor
    resolved = []
    boundaries = []
    for index, lane in enumerate(roadway):
        if index == split:
            if median:
                boundaries.append(PlacementBoundary(roadway[index - 1].lane_id, lane.lane_id,
                                                     cursor, "MEDIAN_EDGE"))
                cursor += median
                boundaries.append(PlacementBoundary(roadway[index - 1].lane_id, lane.lane_id,
                                                     cursor, "MEDIAN_EDGE"))
            else:
                boundaries.append(PlacementBoundary(roadway[index - 1].lane_id, lane.lane_id,
                                                     cursor, "OPPOSING_CENTER"))
        elif index:
            boundaries.append(PlacementBoundary(roadway[index - 1].lane_id, lane.lane_id,
                                                 cursor, "SAME_DIRECTION"))
        resolved.append(PlacementLane(lane.lane_id, lane.direction, lane.width,
                                      cursor + lane.width * 0.5))
        cursor += lane.width
    curb_left, curb_right = lane_left - roadside, cursor + roadside
    width = next((w for w in PLACEMENT_WIDTHS
                  if w >= requested_width and -w * 0.5 + PLACEMENT_MIN_SIDEWALK <= curb_left
                  and curb_right <= w * 0.5 - PLACEMENT_MIN_SIDEWALK), None)
    if width is None:
        raise ValueError("Requested section does not fit the supported grid widths")
    return RoadPlacementSection(
        width, curb_left, curb_right, curb_left + width * 0.5,
        width * 0.5 - curb_right, roadside, roadside, traffic_boundary,
        median_left, median_right, height, tuple(resolved), tuple(boundaries),
    )


def placement_lane_geometry(
    section: RoadPlacementSection, source_lanes: Iterable[LaneLike],
) -> tuple[PlacementLaneGeometry, ...]:
    """Network lane coordinates relative to the same node reference as the mesh.

    NetAI.UpdateLanes adds the lane offset to the native corner reference. This
    output uses the physical surface height, rather than copying legacy lane
    offsets from a different section. Runtime must not derive another layout.
    """
    source = tuple(source_lanes)
    if len({lane.lane_id for lane in source}) != len(source):
        raise ValueError("Placement lane IDs must be unique across the Base Road")
    if tuple((v.lane_id, v.direction, v.width) for v in source if v.zone == "ROAD") != tuple(
        (v.lane_id, v.direction, v.width) for v in section.lanes
    ):
        raise ValueError("Placement section and Base Road lane topology disagree")
    values = {lane.lane_id: PlacementLaneGeometry(lane.lane_id, lane.position, lane.width,
                                                 section.roadway_z) for lane in section.lanes}
    for zone, surface, edge in (
        ("LEFT_SIDEWALK", section.sidewalk_left, -section.total_width * 0.5),
        ("RIGHT_SIDEWALK", section.sidewalk_right, section.curb_right),
    ):
        pedestrians = tuple(lane for lane in source if lane.zone == zone)
        if not pedestrians:
            continue
        width = sidewalk_lane_width(surface, len(pedestrians))
        cursor = edge + SIDEWALK_LANE_TOTAL_INSET * 0.5
        for lane in pedestrians:
            values[lane.lane_id] = PlacementLaneGeometry(lane.lane_id, cursor + width * 0.5, width, 0.0)
            cursor += width
    if any(lane.lane_id not in values for lane in source):
        raise ValueError("Unsupported lane zone in placement Base Road")
    return tuple(values[lane.lane_id] for lane in source)


class VehicleLaneVariant(NamedTuple):
    backward_lanes: int
    forward_lanes: int
    road_id: str
    name: str

    @property
    def vehicle_lane_count(self) -> int:
        return self.backward_lanes + self.forward_lanes


def vehicle_lane_variants(
    max_per_direction: int = 4, max_total: int = 8,
) -> tuple[VehicleLaneVariant, ...]:
    """Return mirror-normalized vehicle-lane variants for the road family."""
    variants = []
    for backward in range(max_per_direction + 1):
        for forward in range(max(backward, 1), max_per_direction + 1):
            if backward + forward > max_total:
                continue
            if backward == 0:
                road_id = f"jp-{forward}l-oneway"
                name = f"JP {forward} Lane One-way"
            else:
                road_id = f"jp-{backward}x{forward}l"
                name = f"JP {backward}+{forward} Lane"
            variants.append(VehicleLaneVariant(backward, forward, road_id, name))
    return tuple(variants)


class ResolvedSetting(NamedTuple):
    value: Any
    source: str


def lane_metadata_defaults(
    lane_type: str, vehicle_type: str,
) -> dict[str, Any] | None:
    """Return defaults only for an exact metadata tuple observed in road data."""
    key = (
        (lane_type or "").strip().upper(),
        (vehicle_type or "").strip().upper(),
    )
    defaults = LANE_METADATA_DEFAULTS.get(key)
    return None if defaults is None else dict(defaults)


def resolve_setting(
    global_value: Any,
    profile_id: str,
    profile_has_override: bool,
    profile_value: Any,
    road_has_override: bool = False,
    road_value: Any = None,
) -> ResolvedSetting:
    """Resolve the fixed global -> profile -> road ownership chain."""
    if road_has_override:
        return ResolvedSetting(road_value, "road override")
    if profile_has_override:
        return ResolvedSetting(profile_value, profile_id)
    return ResolvedSetting(global_value, "global")


def redundant_override(override_value: Any, inherited_value: Any) -> bool:
    """Return whether an explicit override carries no effective difference."""
    if isinstance(override_value, float) or isinstance(inherited_value, float):
        try:
            return abs(float(override_value) - float(inherited_value)) <= 1e-8
        except (TypeError, ValueError):
            return False
    return override_value == inherited_value


def normalize_lane_overrides(
    lane_type: str,
    vehicle_type: str,
    overrides: Mapping[str, Any],
    inherited_values: Mapping[str, Any] | None = None,
) -> dict[str, Any]:
    """Drop only overrides proven equal to the applicable inherited values.

    Exact metadata defaults are used when callers do not provide a more
    specific inherited value. Unknown metadata keeps every explicit value.
    """
    inherited = (
        dict(inherited_values)
        if inherited_values is not None
        else lane_metadata_defaults(lane_type, vehicle_type)
    )
    if inherited is None:
        return dict(overrides)
    return {
        field: value
        for field, value in overrides.items()
        if field not in inherited or not redundant_override(value, inherited[field])
    }


def migrate_authoring_spec_v3_to_v4(
    source: dict[str, Any], profile_id: str,
) -> tuple[dict[str, Any], dict[str, Any]]:
    """Preserve every v3 lane value as an explicit v4 road override.

    The returned IMT mapping is intentionally separate.  The Blender adapter
    owns profile creation because a single imported road must not silently
    mutate an existing shared profile.
    """
    data = deepcopy(source)
    version = int(data.get("schema_version", 2))
    if version >= AUTHORING_SCHEMA_VERSION:
        return data, {}
    legacy_imt = deepcopy(data.get("styles", {}).get("imt_preview", {}))
    data["schema_version"] = AUTHORING_SCHEMA_VERSION
    data["profile_id"] = profile_id
    for lane in data.get("lanes", []):
        overrides = dict(lane.get("overrides", {}))
        for field in LANE_SETTING_FIELDS:
            if field in lane:
                overrides[field] = lane.pop(field)
        if overrides:
            lane["overrides"] = overrides
        else:
            lane.pop("overrides", None)
    styles = data.get("styles")
    if isinstance(styles, dict):
        styles.pop("imt_preview", None)
    return data, legacy_imt


class CrossSectionAllocation(NamedTuple):
    """Resolved use of the fixed space between the two sidewalks."""

    target_width: float
    lane_width: float
    median_width: float
    shoulder_width: float
    actual_width: float
    overflow: float

    @property
    def fits(self) -> bool:
        return self.overflow <= 1e-8


def roadway_depression(enabled: bool) -> float:
    """Return the single standard carriageway depression used by the prototype."""
    return ROADWAY_DEPRESSION if enabled else 0.0


def lane_vertical_offset(zone: str, roadway_is_depressed: bool) -> float:
    """Derive the CS1 lane height from lane zone and the road-level switch."""
    if zone in {"LEFT_SIDEWALK", "RIGHT_SIDEWALK"}:
        return roadway_depression(roadway_is_depressed)
    return 0.0


def sidewalk_lane_width(surface_width: float, lane_count: int = 1) -> float:
    """Fit pedestrian network lanes inside the sidewalk surface."""
    usable_width = max(surface_width - SIDEWALK_LANE_TOTAL_INSET, 0.05)
    return usable_width / max(lane_count, 1)


def allocate_cross_section(
    lanes: Iterable[LaneLike],
    between_sidewalks_width: float,
    median_width: float = 0.0,
) -> CrossSectionAllocation:
    """Allocate a fixed sidewalk-to-sidewalk span without hiding overflow.

    Lane and median dimensions remain authoritative.  Any non-negative
    remainder is divided equally between the two roadside shoulders.  If the
    requested content does not fit, shoulders become zero and ``overflow``
    tells the Blender adapter how much to warn about; generation is not
    rejected and dimensions are not silently shrunk.
    """
    lane_width = max(
        sum(lane.width for lane in lanes if lane.zone == "ROAD"), 0.01
    )
    target_width = max(float(between_sidewalks_width), 0.01)
    resolved_median = max(float(median_width), 0.0)
    required_width = lane_width + resolved_median
    remainder = target_width - required_width
    shoulder_width = max(remainder * 0.5, 0.0)
    overflow = max(-remainder, 0.0)
    return CrossSectionAllocation(
        target_width=target_width,
        lane_width=lane_width,
        median_width=resolved_median,
        shoulder_width=shoulder_width,
        actual_width=max(target_width, required_width),
        overflow=overflow,
    )


def parking_fits(
    lanes: Iterable[LaneLike],
    between_sidewalks_width: float,
    median_width: float,
    parking_lane_width: float = PARKING_LANE_DEFAULT_WIDTH,
) -> bool:
    """Return whether one parking lane on each side fits the fixed span."""
    allocation = allocate_cross_section(
        lanes, between_sidewalks_width, median_width,
    )
    return allocation.shoulder_width + 1e-8 >= max(parking_lane_width, 0.0)


def marking_rule(
    boundary_role: str,
    marking_role: str,
    roadside_lines: bool,
    lane_separator_style: str,
    center_line_style: str,
    line_capable: bool = True,
) -> tuple[bool, str]:
    """Resolve one boundary from the road-level line policy."""
    if boundary_role == "MEDIAN_EDGE":
        return False, "SOLID_WHITE"
    if marking_role == "CARRIAGEWAY_EDGE":
        return bool(roadside_lines and line_capable), "SOLID_WHITE"
    if boundary_role == "LANE_DIVIDER" and marking_role == "CENTER_LINE":
        return True, center_line_style
    if boundary_role == "LANE_DIVIDER" and marking_role == "LANE_SEPARATOR":
        return True, lane_separator_style
    return False, "SOLID_WHITE"


class LaneLike(Protocol):
    lane_id: str
    name: str
    zone: str
    width: float
    direction: str


def median_split_index(lanes: Iterable[LaneLike]) -> int:
    """Return the first boundary between opposing road-lane directions.

    The returned index is relative to the road-lane sequence and identifies
    where the median strip is inserted.  A divided road needs traffic on both
    sides; callers surface the ValueError instead of silently placing a median
    through an arbitrary lane.
    """
    road_lanes = [lane for lane in lanes if lane.zone == "ROAD"]
    for index, (left, right) in enumerate(zip(road_lanes, road_lanes[1:]), 1):
        if left.direction != right.direction:
            return index
    raise ValueError("Median needs adjacent road lanes with opposing directions")


def strip_id(lane: LaneLike) -> str:
    if lane.zone == "LEFT_SIDEWALK":
        return "strip-left-sidewalk"
    if lane.zone == "RIGHT_SIDEWALK":
        return "strip-right-sidewalk"
    return f"strip-{lane.lane_id}"


def expected_boundaries(
    lanes: Iterable[LaneLike], shoulder_width: float, median_enabled: bool = False
) -> list[tuple[str, str, str, str, str, bool, str]]:
    """Derive stable boundary identities and default marking roles.

    The current prototype derives boundary identity from adjacent lane IDs.
    Whether this becomes the final persisted contract remains a design choice.
    """
    road_lanes = [lane for lane in lanes if lane.zone == "ROAD"]
    if not road_lanes:
        return []

    expected: list[tuple[str, str, str, str, str, bool, str]] = []
    left_road_strip = strip_id(road_lanes[0])
    right_road_strip = strip_id(road_lanes[-1])
    if shoulder_width > 1e-8:
        expected.extend(
            (
                (
                    "boundary-left-curb",
                    "Left curb",
                    "CURB",
                    "strip-left-sidewalk",
                    "strip-left-shoulder",
                    False,
                    "CARRIAGEWAY_EDGE",
                ),
                (
                    "boundary-left-carriageway",
                    "Left roadside line",
                    "CARRIAGEWAY_EDGE",
                    "strip-left-shoulder",
                    left_road_strip,
                    True,
                    "CARRIAGEWAY_EDGE",
                ),
            )
        )
    else:
        expected.append(
            (
                "boundary-left-curb",
                "Left curb / roadside",
                "CURB",
                "strip-left-sidewalk",
                left_road_strip,
                True,
                "CARRIAGEWAY_EDGE",
            )
        )

    split_index = median_split_index(road_lanes) if median_enabled else -1
    for boundary_index, (left, right) in enumerate(
        zip(road_lanes, road_lanes[1:]), 1
    ):
        if boundary_index == split_index:
            expected.extend(
                (
                    (
                        "boundary-median-left",
                        "Median left edge",
                        "MEDIAN_EDGE",
                        strip_id(left),
                        "strip-median",
                        False,
                        "CENTER_LINE",
                    ),
                    (
                        "boundary-median-right",
                        "Median right edge",
                        "MEDIAN_EDGE",
                        "strip-median",
                        strip_id(right),
                        False,
                        "CENTER_LINE",
                    ),
                )
            )
            continue
        marking_role = (
            "CENTER_LINE"
            if left.direction != right.direction
            else "LANE_SEPARATOR"
        )
        expected.append(
            (
                f"boundary-{left.lane_id}-{right.lane_id}",
                f"{left.name} / {right.name}",
                "LANE_DIVIDER",
                strip_id(left),
                strip_id(right),
                True,
                marking_role,
            )
        )

    if shoulder_width > 1e-8:
        expected.extend(
            (
                (
                    "boundary-right-carriageway",
                    "Right roadside line",
                    "CARRIAGEWAY_EDGE",
                    right_road_strip,
                    "strip-right-shoulder",
                    True,
                    "CARRIAGEWAY_EDGE",
                ),
                (
                    "boundary-right-curb",
                    "Right curb",
                    "CURB",
                    "strip-right-shoulder",
                    "strip-right-sidewalk",
                    False,
                    "CARRIAGEWAY_EDGE",
                ),
            )
        )
    else:
        expected.append(
            (
                "boundary-right-curb",
                "Right curb / roadside",
                "CURB",
                right_road_strip,
                "strip-right-sidewalk",
                True,
                "CARRIAGEWAY_EDGE",
            )
        )
    return expected


def cross_section_widths(
    lanes: Iterable[LaneLike], shoulder_width: float, sidewalk_width: float,
    median_width: float = 0.0,
) -> tuple[float, float, float]:
    road_lane_width = max(
        sum(lane.width for lane in lanes if lane.zone == "ROAD"), 0.01
    )
    roadway_width = road_lane_width + 2.0 * shoulder_width + max(median_width, 0.0)
    total_width = roadway_width + 2.0 * sidewalk_width
    return roadway_width, total_width, total_width * 0.5
