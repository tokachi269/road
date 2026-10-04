from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image

from tools.validate_texture_layout import (
    BASE_LAYER_ROOT,
    DEFAULT_MANIFEST,
    load_manifest,
    validate_manifest,
)


REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_OUTPUT_ROOT = REPO_ROOT / "textures" / "atlas_bases"


def rebuild_surface_guide_layers(manifest_path: Path = DEFAULT_MANIFEST) -> None:
    """Regenerate flat placement guides, never Photoshop artwork or markings."""
    manifest = load_manifest(manifest_path)
    for layer in manifest["layers"]:
        if not layer["file"].startswith("surface_") or "marking" in layer["file"]:
            continue
        destination = BASE_LAYER_ROOT / layer["file"]
        # These are solid-colour guides. Refuse to erase any authored imagery.
        with Image.open(destination) as existing:
            if len(existing.convert("RGBA").getcolors(existing.width * existing.height) or []) != 1:
                raise ValueError(f"Guide contains artwork; not overwritten: {destination.name}")
        Image.new("RGBA", (int(layer["width_px"]), int(layer["height_px"])),
                  layer["color"]).save(destination)


def build_atlas_bases(
    manifest_path: Path = DEFAULT_MANIFEST,
    output_root: Path = DEFAULT_OUTPUT_ROOT,
) -> list[Path]:
    validate_manifest(manifest_path)
    manifest = load_manifest(manifest_path)
    size = (int(manifest["atlas_width_px"]), int(manifest["texture_height_px"]))
    output_root.mkdir(parents=True, exist_ok=True)

    outputs: list[Path] = []
    for atlas_name, atlas in manifest["atlas_layout"].items():
        canvas = Image.new("RGBA", size, (0, 0, 0, 0))
        for slot in atlas["slots"]:
            for region in slot["regions"]:
                source_path = BASE_LAYER_ROOT / region["file"]
                with Image.open(source_path) as source:
                    layer = source.convert("RGBA")
                canvas.alpha_composite(layer, (int(region["x_px"]), 0))

        output_path = output_root / f"{atlas_name}_base_2048.png"
        canvas.save(output_path, optimize=False)
        outputs.append(output_path)

    return outputs


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Build flat 2048px atlas base PNGs from the positioned base layers."
    )
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT_ROOT)
    parser.add_argument("--rebuild-surface-guides", action="store_true",
                        help="regenerate solid-colour surface placement guides from the manifest")
    args = parser.parse_args()
    if args.rebuild_surface_guides:
        rebuild_surface_guide_layers(args.manifest)
    for output in build_atlas_bases(args.manifest, args.output):
        print(output)


if __name__ == "__main__":
    main()
