import json
from pathlib import Path
import unittest

from PIL import Image, ImageChops

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

    def test_every_texture_region_uses_the_locked_pixel_contract(self):
        expected = {
            "edge.sidewalk": ("surface", 224, 160),
            "curb.upper": ("surface", 384, 6),
            "curb.wall": ("surface", 390, 10),
            "curb.lower": ("surface", 400, 16),
            "edge.asphalt": ("surface", 416, 192),
            "shoulder.default": ("surface", 672, 32),
            "lane.default": ("surface", 768, 192),
            "sidewalk.default": ("surface", 1024, 160),
            "line.solid.white": ("surface", 1987, 26),
            "line.dashed.white": ("surface", 2019, 26),
            "deck.underside": ("structure", 32, 768),
            "elevated.fascia": ("structure", 864, 64),
            "bridge.fascia": ("structure", 992, 96),
            "girder.bottom": ("structure", 1152, 44),
            "girder.side": ("structure", 1280, 134),
            "tunnel.roof": ("tunnel", 32, 768),
            "tunnel.wall": ("tunnel", 864, 320),
        }
        self.assertEqual(set(self.regions), set(expected))
        for region_id, (atlas, x_px, width_px) in expected.items():
            actual = self.regions[region_id]
            self.assertEqual(
                (actual["atlas"], actual["x_px"], actual["width_px"]),
                (atlas, x_px, width_px),
                region_id,
            )

    def test_connected_road_edge_profiles_use_locked_pixel_ranges(self):
        manifest = json.loads(Path(DEFAULT_MANIFEST).read_text(encoding="utf-8"))
        self.assertEqual(
            manifest["uv_profiles"],
            {
                "sidewalk.default+curb.upper": {
                    "atlas": "surface", "x_px": 224, "width_px": 166,
                },
                "curb.lower+shoulder.default": {
                    "atlas": "surface", "x_px": 400, "width_px": 32,
                },
                "curb.lower+lane.default": {
                    "atlas": "surface", "x_px": 400, "width_px": 192,
                },
            },
        )
        self.assertEqual(
            self.regions["edge.sidewalk"]["x_px"]
            + self.regions["edge.sidewalk"]["width_px"],
            self.regions["curb.upper"]["x_px"],
        )
        self.assertEqual(
            self.regions["curb.lower"]["x_px"]
            + self.regions["curb.lower"]["width_px"],
            self.regions["edge.asphalt"]["x_px"],
        )

    def test_generated_atlas_bases_match_every_manifest_region(self):
        root = Path(DEFAULT_MANIFEST).parent
        manifest = json.loads(Path(DEFAULT_MANIFEST).read_text(encoding="utf-8"))
        for atlas_name, atlas in manifest["atlas_layout"].items():
            with Image.open(
                root / "atlas_bases" / f"{atlas_name}_base_2048.png"
            ) as atlas_image:
                actual_atlas = atlas_image.convert("RGBA")
            for slot in atlas["slots"]:
                for region in slot["regions"]:
                    with Image.open(root / "base_layers" / region["file"]) as source:
                        expected = source.convert("RGBA")
                    x_min = region["x_px"]
                    actual = actual_atlas.crop((
                        x_min, 0, x_min + region["width_px"],
                        manifest["texture_height_px"],
                    ))
                    self.assertIsNone(
                        ImageChops.difference(actual, expected).getbbox(),
                        f"{atlas_name}/{region['id']}",
                    )

    def test_normalized_uv_does_not_change_when_atlas_is_halved(self):
        wall = self.regions["curb.wall"]
        self.assertEqual(wall["u_min"], (wall["x_px"] // 2) / 1024)
        self.assertEqual(
            wall["u_max"],
            ((wall["x_px"] + wall["width_px"]) // 2) / 1024,
        )

    def test_curb_adjacent_faces_use_combined_texture_widths(self):
        sidewalk = self.regions["sidewalk.default"]
        upper = self.regions["curb.upper"]
        wall = self.regions["curb.wall"]
        lower = self.regions["curb.lower"]
        shoulder = self.regions["shoulder.default"]
        lane = self.regions["lane.default"]

        sidewalk_start = wall["x_px"] - sidewalk["width_px"] - upper["width_px"]
        shoulder_end = (
            wall["x_px"] + wall["width_px"]
            + lower["width_px"] + shoulder["width_px"]
        )
        lane_end = (
            wall["x_px"] + wall["width_px"]
            + lower["width_px"] + lane["width_px"]
        )
        self.assertEqual(sidewalk_start, 224)
        self.assertEqual(shoulder_end, 448)
        self.assertEqual(lane_end, 608)

    def test_line_band_keeps_internal_alpha_margin_in_a_32px_slot(self):
        solid = self.regions["line.solid.white"]
        dashed = self.regions["line.dashed.white"]
        self.assertEqual((solid["x_px"], solid["width_px"]), (1987, 26))
        self.assertEqual((dashed["x_px"], dashed["width_px"]), (2019, 26))
        self.assertEqual(solid["x_px"] + solid["width_px"] // 2, 2000)
        self.assertEqual(dashed["x_px"] + dashed["width_px"] // 2, 2032)
        self.assertEqual(solid["u_min"], (solid["x_px"] / 2) / 1024)
        self.assertEqual(dashed["u_min"], (dashed["x_px"] / 2) / 1024)

    def test_photoshop_generator_outputs_are_explicit_and_complete(self):
        manifest = json.loads(Path(DEFAULT_MANIFEST).read_text(encoding="utf-8"))
        generator = manifest["photoshop_generator"]
        self.assertEqual(generator["assets_directory"], "road-assets")
        self.assertEqual(
            generator["maps"],
            {
                "d": {"shader_property": "_MainTex", "required": True},
                "a": {"required": False},
                "p": {"required": False},
                "r": {"required": False},
                "n": {"required": False},
                "s": {"required": False},
            },
        )
        self.assertEqual(
            generator["runtime_packs"],
            {
                "APR": {
                    "shader_property": "_APRMap",
                    "source_maps": ["a", "p", "r"],
                },
                "XYS": {
                    "shader_property": "_XYSMap",
                    "source_maps": ["n", "s"],
                },
            },
        )
        expected_maps = {
            map_id: f"{{family}}_{map_id}.png"
            for map_id in ("d", "a", "p", "r", "n", "s")
        }
        self.assertEqual(
            generator["families"],
            {
                family: {
                    map_id: filename.format(family=family)
                    for map_id, filename in expected_maps.items()
                }
                for family in ("surface", "structure", "tunnel")
            },
        )


if __name__ == "__main__":
    unittest.main()
