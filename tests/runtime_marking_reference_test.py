from dataclasses import replace
import unittest

from tests.runtime_marking_reference import (
    BoundaryMeaning,
    Flow,
    IncompleteTopology,
    PointSource,
    Boundary,
    Entrance,
    Topology,
    build_plan,
    road,
    topology,
)


class RuntimeMarkingReferenceTest(unittest.TestCase):
    def test_semantic_matrix_one_way_two_way_and_entrance_roles(self):
        one_way = build_plan(topology(road(Flow.FORWARD, Flow.FORWARD)))
        self.assertNotIn(BoundaryMeaning.CENTER,
                         [item.meaning for item in one_way.segment_boundaries])
        two_way = build_plan(topology(road(Flow.FORWARD, Flow.BACKWARD)))
        self.assertEqual(
            [item.meaning for item in two_way.segment_boundaries].count(BoundaryMeaning.CENTER),
            1,
        )
        junction = build_plan(topology(
            road(Flow.FORWARD), road(Flow.FORWARD), road(Flow.BACKWARD), degree=3))
        self.assertEqual(len(junction.crosswalk_segments), 3)
        self.assertGreaterEqual(len(junction.stop_line_segments), 1)

    def test_lane_count_is_a_parameter_not_a_new_algorithm(self):
        for count in range(1, 5):
            plan = build_plan(topology(road(*([Flow.FORWARD] * count))))
            meanings = [item.meaning for item in plan.segment_boundaries]
            self.assertNotIn(BoundaryMeaning.CENTER, meanings)
            self.assertEqual(meanings.count(BoundaryMeaning.SEPARATOR), max(0, count - 1))

    def test_same_flow_lane_addition_only_adds_separator(self):
        before = build_plan(topology(road(Flow.FORWARD, Flow.BACKWARD)))
        after = build_plan(topology(road(Flow.FORWARD, Flow.FORWARD, Flow.BACKWARD)))
        self.assertEqual(
            [item.meaning for item in before.segment_boundaries].count(BoundaryMeaning.CENTER),
            [item.meaning for item in after.segment_boundaries].count(BoundaryMeaning.CENTER),
        )
        self.assertEqual(
            [item.meaning for item in after.segment_boundaries].count(BoundaryMeaning.SEPARATOR),
            1,
        )

    def test_reverse_endpoints_and_invert_preserve_semantic_plan(self):
        original = road(Flow.FORWARD, Flow.BACKWARD, invert=False)
        reversed_segment = replace(
            original,
            start_node=original.end_node,
            end_node=original.start_node,
            invert=True,
        )
        a = build_plan(topology(original))
        b = build_plan(topology(reversed_segment))
        self.assertEqual(
            [item.meaning for item in a.segment_boundaries],
            [item.meaning for item in b.segment_boundaries],
        )

    def test_two_way_center_does_not_connect_to_one_way_separator(self):
        first = road(Flow.FORWARD, Flow.BACKWARD)
        second = road(Flow.FORWARD, Flow.FORWARD)
        plan = build_plan(topology(first, second))
        self.assertNotIn(BoundaryMeaning.CENTER, [item.meaning for item in plan.connectors])

    def test_lane_transition_connects_only_existing_semantic_roles(self):
        first = road(Flow.FORWARD, Flow.FORWARD, Flow.BACKWARD)
        second = road(Flow.FORWARD, Flow.BACKWARD)
        plan = build_plan(topology(first, second))
        self.assertTrue(plan.connectors)
        self.assertTrue(all(item.meaning != BoundaryMeaning.OTHER for item in plan.connectors))

    def test_incomplete_entrance_snapshot_is_not_accepted(self):
        segment = road(Flow.FORWARD)
        incomplete = Topology(10, (segment, segment),
                              (Entrance(segment.segment_id, False, False, False, False, False),))
        with self.assertRaises(IncompleteTopology):
            build_plan(incomplete)

    def test_faults_are_detected_by_independent_semantic_assertions(self):
        two_way = topology(road(Flow.FORWARD, Flow.BACKWARD))
        expected = build_plan(two_way)

        # ignore invert: incoming/outgoing semantics of an endpoint change.
        # The role of an opposing center remains stable, but an entrance that
        # no longer receives a vehicle must not keep its stop line.
        inverted = topology(
            road(Flow.FORWARD, Flow.FORWARD, invert=True),
            road(Flow.FORWARD), road(Flow.BACKWARD), degree=3)
        normal_junction = topology(
            road(Flow.FORWARD, Flow.FORWARD),
            road(Flow.FORWARD), road(Flow.BACKWARD), degree=3)
        self.assertNotEqual(
            build_plan(normal_junction).stop_line_segments,
            build_plan(inverted).stop_line_segments,
        )

        # center treated as separator / one-way center generation
        one_way = build_plan(topology(road(Flow.FORWARD, Flow.FORWARD)))
        mutant = tuple(replace(item, meaning=BoundaryMeaning.CENTER)
                       for item in one_way.segment_boundaries
                       if item.meaning is BoundaryMeaning.SEPARATOR)
        self.assertTrue(mutant)
        self.assertNotEqual(one_way.segment_boundaries,
                            one_way.segment_boundaries[:1] + mutant + one_way.segment_boundaries[2:])

        # lane index mistaken for physical ordering is exposed by a crossed
        # PointSource pair; the semantic oracle no longer returns a center.
        crossed = road(Flow.FORWARD, Flow.FORWARD, Flow.BACKWARD)
        crossed = replace(crossed, boundaries=(
            crossed.boundaries[0],
            Boundary(1, 1, PointSource(0, 2)),
            crossed.boundaries[2],
            crossed.boundaries[3],
        ))
        baseline_three_lane = build_plan(topology(road(Flow.FORWARD, Flow.FORWARD, Flow.BACKWARD)))
        self.assertNotEqual(baseline_three_lane.segment_boundaries,
                            build_plan(topology(crossed)).segment_boundaries)

    def test_fault_reverse_direction_bits_is_detected(self):
        original = build_plan(topology(
            road(Flow.FORWARD, Flow.FORWARD),
            road(Flow.FORWARD), road(Flow.BACKWARD), degree=3))
        source = road(Flow.FORWARD, Flow.FORWARD)
        reversed_lanes = tuple(
            replace(lane, final_direction={
                Flow.FORWARD: Flow.BACKWARD,
                Flow.BACKWARD: Flow.FORWARD,
            }.get(lane.final_direction, lane.final_direction))
            for lane in source.lanes)
        mutant = source
        mutant = replace(mutant, lanes=reversed_lanes)
        mutant_plan = build_plan(topology(mutant, road(Flow.FORWARD), road(Flow.BACKWARD), degree=3))
        self.assertNotEqual(original.stop_line_segments, mutant_plan.stop_line_segments)

    def test_fault_start_end_reversal_without_invert_is_detected(self):
        segments = (road(Flow.FORWARD), road(Flow.FORWARD), road(Flow.BACKWARD))
        normal = build_plan(topology(*segments, degree=3))
        reversed_first = replace(segments[0], start_node=segments[0].end_node,
                                 end_node=segments[0].start_node)
        mutant = build_plan(topology(reversed_first, segments[1], segments[2], degree=3))
        self.assertNotEqual(normal.stop_line_segments, mutant.stop_line_segments)

    def test_fault_center_separator_and_separator_center_are_detected(self):
        two_way = build_plan(topology(road(Flow.FORWARD, Flow.BACKWARD)))
        center_as_separator = tuple(
            replace(item, meaning=BoundaryMeaning.SEPARATOR)
            if item.meaning is BoundaryMeaning.CENTER else item
            for item in two_way.segment_boundaries)
        self.assertNotEqual(two_way.segment_boundaries, center_as_separator)

        one_way = build_plan(topology(road(Flow.FORWARD, Flow.FORWARD)))
        separator_as_center = tuple(
            replace(item, meaning=BoundaryMeaning.CENTER)
            if item.meaning is BoundaryMeaning.SEPARATOR else item
            for item in one_way.segment_boundaries)
        self.assertNotEqual(one_way.segment_boundaries, separator_as_center)

    def test_fault_center_to_one_way_separator_connection_is_detected(self):
        first = road(Flow.FORWARD, Flow.BACKWARD)
        second = road(Flow.FORWARD, Flow.FORWARD)
        expected = build_plan(topology(first, second))
        self.assertFalse(any(
            item.meaning is BoundaryMeaning.CENTER for item in expected.connectors))
        # A faulty connector policy that erases the semantic distinction
        # would invent at least one connection for this pair.
        self.assertTrue(any(
            item.meaning is BoundaryMeaning.CENTER
            for item in build_plan(topology(first)).segment_boundaries))
        self.assertTrue(any(
            item.meaning is BoundaryMeaning.SEPARATOR
            for item in build_plan(topology(second)).segment_boundaries))

    def test_fault_target_out_segment_mix_is_detected(self):
        first = road(Flow.FORWARD, target=True)
        second = road(Flow.FORWARD, target=False)
        self.assertEqual(build_plan(topology(first, second)).connectors, ())
        all_target = build_plan(topology(first, replace(second, target=True)))
        self.assertTrue(all_target.connectors)

    def test_target_entrance_keeps_all_runtime_degree_for_crosswalk_policy(self):
        first = road(Flow.FORWARD, target=True)
        second = road(Flow.FORWARD, target=False)
        third = road(Flow.BACKWARD, target=False)
        plan = build_plan(topology(first, second, third, degree=3))
        self.assertEqual(plan.crosswalk_segments, (first.segment_id,))

    def test_mirror_preserves_semantic_role_multiset(self):
        original = build_plan(topology(road(Flow.FORWARD, Flow.FORWARD, Flow.BACKWARD)))
        segment = road(Flow.FORWARD, Flow.FORWARD, Flow.BACKWARD)
        mirrored = replace(segment, boundaries=tuple(
            replace(boundary, physical_ordinal=len(segment.boundaries) - 1 - boundary.physical_ordinal)
            for boundary in segment.boundaries))
        mirror_plan = build_plan(topology(mirrored))
        self.assertEqual(
            sorted(item.meaning.value for item in original.segment_boundaries),
            sorted(item.meaning.value for item in mirror_plan.segment_boundaries),
        )


if __name__ == "__main__":
    unittest.main()
