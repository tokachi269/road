import unittest

from tools.validate_texture_layout import DEFAULT_MANIFEST, validate_manifest


class TextureLayoutTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.regions = validate_manifest(DEFAULT_MANIFEST)

    def test_curb_profile_is_contiguous_and_uses_expected_ranges(self):
        upper = self.regions["curb.upper"]
        wall = self.regions["curb.wall"]
        lower = self.regions["curb.lower"]

        self.assertEqual((upper["x_px"], upper["width_px"]), (384, 6))
        self.assertEqual((wall["x_px"], wall["width_px"]), (390, 10))
        self.assertEqual((lower["x_px"], lower["width_px"]), (400, 16))
        self.assertEqual(upper["x_px"] + upper["width_px"], wall["x_px"])
        self.assertEqual(wall["x_px"] + wall["width_px"], lower["x_px"])

    def test_normalized_uv_does_not_change_when_atlas_is_halved(self):
        wall = self.regions["curb.wall"]
        self.assertEqual(wall["u_min"], (wall["x_px"] // 2) / 1024)
        self.assertEqual(
            wall["u_max"],
            ((wall["x_px"] + wall["width_px"]) // 2) / 1024,
        )

    def test_line_band_keeps_internal_alpha_margin_in_a_32px_slot(self):
        dashed = self.regions["line.dashed.white"]
        self.assertEqual((dashed["x_px"], dashed["width_px"]), (2022, 26))
        self.assertEqual((dashed["x_px"] // 2, dashed["width_px"] // 2), (1011, 13))


if __name__ == "__main__":
    unittest.main()
