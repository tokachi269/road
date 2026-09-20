"""Blender-independent preview geometry planning."""

from __future__ import annotations

from dataclasses import dataclass
@dataclass(frozen=True)
class GirderLayout:
    count: int
    centers: tuple[float, ...]
    width: float
    depth: float
    spacing: float
    outer_offset: float
    reference_span: float
    standard_name: str


# JIS A 5373 PC compo bridge modules quoted by MLIT Kanto (2025), page 38:
# https://www.ktr.mlit.go.jp/ktr_content/content/000941869.pdf
# The depths are the published values for a 35 m span.  The 0.70 m rectangle
# is the lower-flange overall width in the standard main-girder section shown
# by the Japan Prestressed Concrete Institute; Blender currently uses that
# envelope as a deliberately simple rectangular preview:
# https://jpci.or.jp/data/pc-seminar/text/026_pc-seminar-text_199802.pdf
_REFERENCE_SPAN_M = 35.0
_RECTANGULAR_ENVELOPE_WIDTH_M = 0.70
_STANDARD_MODULES = (
    # center spacing, girder depth at 35 m, reference edge offset
    (2.60, 1.80, 0.80),
    (3.20, 2.10, 1.20),
    (3.80, 2.50, 1.05),
)


def plan_main_girders(
    deck_width: float,
) -> GirderLayout:
    """Choose one published PC-compo module that fits an even girder count.

    Spacing and depth are never stretched to fit.  The module whose remaining
    edge offset is closest to its published example is selected, so changing
    road width changes the module/count together and cannot push a girder
    outside the deck.
    """
    if deck_width <= 0.0:
        raise ValueError("deck_width must be positive")

    candidates = []
    maximum_count = max(2, int(deck_width / _STANDARD_MODULES[0][0]) + 4)
    for spacing, depth, reference_outer_offset in _STANDARD_MODULES:
        for count in range(2, maximum_count + 1, 2):
            occupied_width = (count - 1) * spacing
            outer_offset = (deck_width - occupied_width) * 0.5
            if outer_offset + 1e-9 < _RECTANGULAR_ENVELOPE_WIDTH_M * 0.5:
                continue
            candidates.append(
                (
                    abs(outer_offset - reference_outer_offset),
                    spacing,
                    count,
                    depth,
                    outer_offset,
                )
            )

    if not candidates:
        raise ValueError("deck is too narrow for the standard girder envelope")

    _, spacing, count, depth, outer_offset = min(candidates)
    first_center = -deck_width * 0.5 + outer_offset
    centers = tuple(first_center + spacing * index for index in range(count))

    return GirderLayout(
        count=count,
        centers=centers,
        width=_RECTANGULAR_ENVELOPE_WIDTH_M,
        depth=depth,
        spacing=spacing,
        outer_offset=outer_offset,
        reference_span=_REFERENCE_SPAN_M,
        standard_name="JIS A 5373 PC compo / 35 m reference",
    )
