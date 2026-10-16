"""PNG-level expectations independent of the drawing implementation."""
import json
from pathlib import Path
import shutil
import tempfile
import unittest

from PIL import Image
from tools import generate_road_icons as icons


class RoadIconTest(unittest.TestCase):
    def setUp(self):
        self.settings, self.font, _ = icons.load_settings()

    def config(self, root, **changes):
        data = dict(self.settings)
        shutil.copyfile(icons.DEFAULT_CONFIG.parent / data["font"], root / "font.ttf")
        data["font"] = "font.ttf"
        data.update(changes)
        path = root / "settings.json"
        path.write_text(json.dumps(data), encoding="utf-8")
        return path

    def test_all_14_topologies_white_center_lht_arrows_and_smooth_type(self):
        pairs = [(0,1),(0,2),(0,3),(0,4),(1,1),(2,2),(3,3),(4,4),
                 (1,2),(1,3),(1,4),(2,3),(2,4),(3,4)]
        expected = ["→1","→2","→3","→4","1+1","2+2","3+3","4+4",
                    "1+2","1+3","1+4","2+3","2+4","3+4"]
        columns = {1:[64],2:[54,74],3:[47,64,81],4:[40,56,72,88],
                   5:[34,49,64,79,94],6:[27,42,57,71,86,101],
                   7:[21,35,50,64,78,93,107],8:[19,32,45,58,71,84,97,110]}
        self.assertEqual({(v.backward_lanes,v.forward_lanes) for v in icons.variants()}, set(pairs))
        for (b,f), label in zip(pairs, expected):
            with self.subTest(label=label):
                self.assertEqual(icons.label(b,f), label)
                image = icons.render(b,f,self.settings,self.font)
                self.assertEqual(image.size, (128,128))
                self.assertEqual(image.getpixel((0,0))[3], 0)
                def paint(x,y):
                    return min(image.getpixel((x,y))[:3]) > 180
                for i,x in enumerate(columns[b+f]):
                    down = b > 0 and i >= b
                    head = sum(image.getpixel((xx,58 if down else 51))[0] for xx in range(x-3,x+4))
                    shaft = sum(image.getpixel((xx,51 if down else 58))[0] for xx in range(x-3,x+4))
                    self.assertGreater(head, shaft+150)
                # At y=19 all dashed separators are in a gap, before arrows.
                # Only two white edges and (for two-way) a white center remain.
                row = [paint(x,19) for x in range(128)]
                runs = sum(value and (x==0 or not row[x-1]) for x,value in enumerate(row))
                self.assertEqual(runs, 2 + bool(b and f))
                self.assertFalse(any(r>230 and 150<g<220 and blue<100 for r,g,blue,a in image.getdata()))
                self.assertTrue(any(40<image.getpixel((x,y))[0]<220
                                    for y in range(108,124) for x in range(40,88)))

    def test_import_five_states_and_runtime_are_identical_pngs(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            config = self.config(root, import_job_names={"jp-1l-oneway":"Custom Job"})
            icons.generate(root/"assets", config, root/"preview")
            self.assertEqual(len(list((root/"assets/import").glob("*.png"))), 70)
            for v in icons.variants():
                name = f"{v.backward_lanes}-{v.forward_lanes}.png"
                png = (root/"assets"/name).read_bytes()
                self.assertEqual((root/"preview/icons"/name).read_bytes(), png)
                job = "Custom Job" if v.road_id == "jp-1l-oneway" else v.road_id
                # Literal native RoadImporter suffixes, not production STATES.
                for suffix in ("thumb","hovered","pressed","focused","disabled"):
                    self.assertEqual((root/"assets/import"/f"{job}_{suffix}.png").read_bytes(), png)

    def test_footer_height_and_text_size_are_independently_effective(self):
        original = icons.render(1,2,self.settings,self.font)
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            tall,font,_ = icons.load_settings(self.config(root,footer_height=28))
            image = icons.render(1,2,tall,font)
            self.assertEqual(original.getpixel((5,100)), (37,40,42,255))
            self.assertEqual(image.getpixel((5,100)), (26,29,31,255))
            small,font,_ = icons.load_settings(self.config(root,font_size=12))
            image = icons.render(1,2,small,font)
            self.assertEqual(original.crop((0,0,128,106)).tobytes(), image.crop((0,0,128,106)).tobytes())
            self.assertNotEqual(original.crop((0,106,128,128)).tobytes(), image.crop((0,106,128,128)).tobytes())

    def test_overlay_alpha_and_orientation(self):
        original = icons.render(1,1,self.settings,self.font)
        overlay = Image.new("RGBA", (128,128))
        overlay.putpixel((0,0), (8,16,24,64))
        overlay.putpixel((5,5), (220,140,60,128))
        image = icons.render(1,1,self.settings,self.font,overlay)
        self.assertEqual(image.getpixel((0,0)), (8,16,24,64))
        self.assertEqual(image.getpixel((5,5)), (129,90,51,255))
        self.assertEqual(image.getpixel((5,122)), original.getpixel((5,122)))
        self.assertEqual(icons.render(1,1,self.settings,self.font,Image.new("RGBA",(128,128))).tobytes(), original.tobytes())

    def test_bad_settings_do_not_replace_outputs(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            output = root / "assets"
            output.mkdir()
            sentinel = output / "1-1.png"
            sentinel.write_bytes(b"preserve previous artwork")
            for change in ({"footer_height":12,"font_size":32}, {"font_size":True},
                           {"overlay":"missing.png"}, {"import_job_names":{"jp-1l-oneway":"../escape"}},
                           {"import_job_names":{"jp-1l-oneway":"JP-2L-ONEWAY"}}):
                with self.subTest(change=change), self.assertRaises((ValueError,OSError)):
                    icons.generate(output, self.config(root,**change))
                self.assertEqual(sentinel.read_bytes(), b"preserve previous artwork")
            Image.new("RGBA",(256,128)).save(root/"large.png")
            with self.assertRaises(ValueError):
                icons.load_settings(self.config(root,overlay="large.png"))


if __name__ == "__main__":
    unittest.main()
