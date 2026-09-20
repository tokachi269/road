from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import unittest


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "blender_addon" / "road_builder" / "geometry_plan.py"
SPEC = importlib.util.spec_from_file_location("road_builder_geometry_plan", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
geometry_plan = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = geometry_plan
SPEC.loader.exec_module(geometry_plan)


class GeometryPlanTest(unittest.TestCase):
    def test_standard_module_selection_keeps_girder_count_even(self) -> None:
        cases = (
            (8.0, 2, 3.8),
            (12.0, 4, 3.2),
            (17.75, 6, 3.2),
            (21.0, 6, 3.8),
            (27.5, 8, 3.8),
        )
        for deck_width, expected_count, expected_spacing in cases:
            with self.subTest(deck_width=deck_width):
                layout = geometry_plan.plan_main_girders(deck_width)
                self.assertEqual(layout.count, expected_count)
                self.assertEqual(layout.count % 2, 0)
                self.assertEqual(layout.spacing, expected_spacing)

    def test_girders_are_symmetric_and_inside_deck(self) -> None:
        layout = geometry_plan.plan_main_girders(21.0)
        for left, right in zip(layout.centers, reversed(layout.centers)):
            self.assertAlmostEqual(left, -right)
        self.assertTrue(
            all(
                abs(center) + layout.width * 0.5 < 21.0 * 0.5
                for center in layout.centers
            )
        )

    def test_rectangular_envelope_uses_published_reference_dimensions(self) -> None:
        layout = geometry_plan.plan_main_girders(12.0)
        self.assertEqual(layout.width, 0.70)
        self.assertEqual(layout.depth, 2.10)
        self.assertEqual(layout.spacing, 3.20)
        self.assertAlmostEqual(layout.outer_offset, 1.20)
        self.assertEqual(layout.reference_span, 35.0)

    def test_wider_deck_changes_module_without_stretching_spacing(self) -> None:
        layout = geometry_plan.plan_main_girders(21.0)
        self.assertEqual(layout.count, 6)
        self.assertEqual(layout.spacing, 3.80)
        self.assertAlmostEqual(layout.outer_offset, 1.00)

    def test_supported_width_range_never_places_rectangle_outside_deck(self) -> None:
        for quarter_meters in range(24, 161):
            deck_width = quarter_meters * 0.25
            with self.subTest(deck_width=deck_width):
                layout = geometry_plan.plan_main_girders(deck_width)
                self.assertTrue(
                    all(
                        abs(center) + layout.width * 0.5
                        <= deck_width * 0.5 + 1e-9
                        for center in layout.centers
                    )
                )


if __name__ == "__main__":
    unittest.main()
