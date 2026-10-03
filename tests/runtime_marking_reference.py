"""Independent semantic oracle for Runtime marking tests.

This module intentionally does not import RoadRuntimeHost.Runtime.  Its input
is a small transcription of the values available at the CS1/IMT boundary:
lane final directions, segment endpoints/invert, node connections, and IMT
point-source lane indices.  The oracle describes road meaning, not the
production enum or its role derivation.
"""

from dataclasses import dataclass, field, replace
from enum import Enum
from typing import Iterable, Optional, Tuple


class Flow(Enum):
    NONE = "none"
    FORWARD = "forward"
    BACKWARD = "backward"
    BOTH = "both"


class BoundaryMeaning(Enum):
    OTHER = "other"
    ROADSIDE = "roadside"
    SEPARATOR = "same_direction_separator"
    CENTER = "opposing_center"


@dataclass(frozen=True)
class Lane:
    index: int
    final_direction: Flow
    vehicle: bool = True
    pedestrian: bool = False


@dataclass(frozen=True)
class PointSource:
    left_index: int
    right_index: int


@dataclass(frozen=True)
class Boundary:
    point_index: int
    physical_ordinal: int
    source: PointSource


@dataclass(frozen=True)
class Segment:
    segment_id: int
    start_node: int
    end_node: int
    invert: bool
    lanes: Tuple[Lane, ...]
    boundaries: Tuple[Boundary, ...]
    target: bool = True


@dataclass(frozen=True)
class Entrance:
    segment_id: int
    is_start_side: bool
    pedestrian_lane: bool
    crossing_allowed: bool
    incoming_vehicle_lane: bool
    traffic_control: bool


@dataclass(frozen=True)
class Topology:
    node_id: int
    segments: Tuple[Segment, ...]
    entrances: Tuple[Entrance, ...]


@dataclass(frozen=True)
class BoundaryPlan:
    segment_id: int
    point_index: int
    meaning: BoundaryMeaning


@dataclass(frozen=True)
class ConnectorPlan:
    first_segment_id: int
    first_point_index: int
    second_segment_id: int
    second_point_index: int
    meaning: BoundaryMeaning


@dataclass(frozen=True)
class MarkingPlan:
    segment_boundaries: Tuple[BoundaryPlan, ...] = ()
    connectors: Tuple[ConnectorPlan, ...] = ()
    crosswalk_segments: Tuple[int, ...] = ()
    stop_line_segments: Tuple[int, ...] = ()


class IncompleteTopology(ValueError):
    pass


def _flip(flow: Flow) -> Flow:
    if flow is Flow.FORWARD:
        return Flow.BACKWARD
    if flow is Flow.BACKWARD:
        return Flow.FORWARD
    return flow


def _at_node(flow: Flow, segment: Segment, node_id: int) -> Flow:
    # finalDirection is segment-oriented.  Invert changes that orientation;
    # endpoint selection is retained in the raw model for entrance semantics.
    return _flip(flow) if segment.invert else flow


def _lane(segment: Segment, index: int) -> Optional[Lane]:
    return next((lane for lane in segment.lanes if lane.index == index), None)


def boundary_meaning(segment: Segment, boundary: Boundary, node_id: int) -> BoundaryMeaning:
    if boundary.source.left_index < 0 or boundary.source.right_index < 0:
        return BoundaryMeaning.ROADSIDE
    left = _lane(segment, boundary.source.left_index)
    right = _lane(segment, boundary.source.right_index)
    if left is None or right is None:
        return BoundaryMeaning.OTHER
    left_flow = _at_node(left.final_direction, segment, node_id)
    right_flow = _at_node(right.final_direction, segment, node_id)
    one_way = {Flow.FORWARD, Flow.BACKWARD}
    if left_flow in one_way and right_flow in one_way:
        return (BoundaryMeaning.CENTER if left_flow is not right_flow
                else BoundaryMeaning.SEPARATOR)
    if Flow.BOTH in (left_flow, right_flow):
        return BoundaryMeaning.SEPARATOR
    return BoundaryMeaning.OTHER


def _ordered_boundaries(segment: Segment) -> Tuple[Boundary, ...]:
    return tuple(sorted(segment.boundaries, key=lambda item: item.physical_ordinal))


def _segment_plan(segment: Segment, node_id: int) -> Tuple[BoundaryPlan, ...]:
    return tuple(
        BoundaryPlan(segment.segment_id, boundary.point_index,
                     boundary_meaning(segment, boundary, node_id))
        for boundary in _ordered_boundaries(segment)
    )


def _connectors(first: Segment, second: Segment, node_id: int) -> Tuple[ConnectorPlan, ...]:
    left = _segment_plan(first, node_id)
    right = _segment_plan(second, node_id)
    used = set()
    result = []
    for boundary in left:
        if boundary.meaning in (BoundaryMeaning.OTHER, BoundaryMeaning.ROADSIDE):
            allowed = {BoundaryMeaning.ROADSIDE} if boundary.meaning is BoundaryMeaning.ROADSIDE else set()
        else:
            allowed = {boundary.meaning}
        chosen = None
        for index in range(len(right) - 1, -1, -1):
            candidate = right[index]
            if index in used or candidate.meaning not in allowed:
                continue
            chosen = (index, candidate)
            break
        if chosen is None:
            continue
        index, candidate = chosen
        used.add(index)
        result.append(ConnectorPlan(boundary.segment_id, boundary.point_index,
                                    candidate.segment_id, candidate.point_index,
                                    boundary.meaning))
    return tuple(result)


def build_plan(topology: Topology) -> MarkingPlan:
    if len(topology.entrances) != len(topology.segments):
        raise IncompleteTopology(
            f"segments={len(topology.segments)} entrances={len(topology.entrances)}")
    all_segments = tuple(topology.segments)
    segments = tuple(segment for segment in all_segments if segment.target)
    boundaries = tuple(
        boundary_plan
        for segment in segments
        for boundary_plan in _segment_plan(segment, topology.node_id))
    connectors = ()
    if len(segments) == 2 and len(topology.entrances) == 2:
        connectors = _connectors(segments[0], segments[1], topology.node_id)
    crosswalks = ()
    stops = ()
    if len(all_segments) >= 3:
        crosswalks = tuple(
            entrance.segment_id for entrance in topology.entrances
            if entrance.segment_id in {segment.segment_id for segment in segments}
            and entrance.pedestrian_lane and entrance.crossing_allowed)
        stops = tuple(
            entrance.segment_id for entrance in topology.entrances
            if entrance.segment_id in {segment.segment_id for segment in segments}
            if _has_incoming_vehicle(
                next(segment for segment in all_segments
                     if segment.segment_id == entrance.segment_id),
                entrance, topology.node_id)
            and entrance.traffic_control)
    return MarkingPlan(boundaries, connectors, crosswalks, stops)


def road(*flows: Flow, invert: bool = False, node: int = 10, target: bool = True) -> Segment:
    lanes = tuple(Lane(index, flow) for index, flow in enumerate(flows))
    boundaries = [Boundary(0, 0, PointSource(-1, 0))]
    for index in range(len(lanes) - 1):
        boundaries.append(Boundary(index + 1, index + 1,
                                   PointSource(index, index + 1)))
    boundaries.append(Boundary(len(lanes), len(lanes),
                               PointSource(len(lanes) - 1, -1)))
    return Segment(node, 1, node, invert, lanes, tuple(boundaries), target)


def topology(*segments: Segment, degree: Optional[int] = None) -> Topology:
    node_id = 10
    normalized = []
    used = set()
    for index, segment in enumerate(segments, 1):
        segment_id = segment.segment_id
        if segment_id in used:
            segment_id = index
        used.add(segment_id)
        normalized.append(replace(segment, segment_id=segment_id))
    segments = tuple(normalized)
    entrances = tuple(
        Entrance(segment.segment_id, segment.start_node == node_id,
                 False, False, False, False)
        for segment in segments)
    if degree is not None:
        entrances = tuple(
            Entrance(entrance.segment_id, entrance.is_start_side,
                     degree >= 3, degree >= 3,
                     any(lane.vehicle and lane.final_direction in (Flow.FORWARD, Flow.BACKWARD)
                         for lane in segment.lanes),
                     degree >= 3)
            for entrance, segment in zip(entrances, segments))
    return Topology(node_id, tuple(segments), entrances)


def _has_incoming_vehicle(segment: Segment, entrance: Entrance, node_id: int) -> bool:
    toward_node = Flow.BACKWARD if entrance.is_start_side else Flow.FORWARD
    if segment.invert:
        toward_node = _flip(toward_node)
    return any(
        lane.vehicle and lane.final_direction in (toward_node, Flow.BOTH)
        for lane in segment.lanes
    )
