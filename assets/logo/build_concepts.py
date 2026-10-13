"""Builds the three first-round logo concepts as black SVGs on a 256 x 256 canvas.

A  P-rewind   letter P whose bowl is a counter-clockwise rewind arrow
B  hunter eye camera lens ring + iris with a vertical slit pupil (negative space)
C  claw rewind two rewind chevrons whose arms taper like claw marks
"""

import math
from pathlib import Path

OUT = Path(__file__).parent / "concepts"
OUT.mkdir(exist_ok=True)
INK = "#111111"


def svg(title: str, body: str) -> str:
    return (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256" '
        f'role="img" aria-labelledby="title"><title id="title">{title}</title>{body}</svg>\n'
    )


def f(v: float) -> str:
    return f"{v:.2f}".rstrip("0").rstrip(".")


# ---- A: P-rewind -------------------------------------------------------------
# Stroke 32 everywhere. Stem centre x=64, from y=48 to y=216.
# Bowl: from the stem at y=160 a line runs right to x=144, a semicircle (r=56, centre 144,104) climbs the right side
# counter-clockwise to the top, then ends in a rounded arrowhead pointing back at the stem (the rewind).
def concept_a() -> str:
    w = 32
    stem = f'<path d="M64 48 V216" stroke="{INK}" stroke-width="{w}" stroke-linecap="round" fill="none"/>'
    bowl = (f'<path d="M64 160 H144 A56 56 0 0 0 144 48" stroke="{INK}" stroke-width="{w}" '
            'stroke-linecap="butt" stroke-linejoin="round" fill="none"/>')
    # Arrowhead pointing left: tip at x=108, base at x=152, height 72.
    head = (f'<path d="M108 48 L152 12 L152 84 Z" fill="{INK}" stroke="{INK}" stroke-width="10" '
            'stroke-linejoin="round"/>')
    return svg("PoPreyClip concept A: P rewind", stem + bowl + head)


# ---- B: hunter eye -------------------------------------------------------------
def concept_b() -> str:
    ring = f'<circle cx="128" cy="128" r="102" fill="none" stroke="{INK}" stroke-width="24"/>'
    # Iris disc r=62 with an almond (vertical slit) pupil cut out: chord 112, half-width 20.
    half_chord, sag = 54, 18
    radius = (half_chord ** 2 + sag ** 2) / (2 * sag)
    top, bottom = 128 - half_chord, 128 + half_chord
    iris = (
        f'<path fill="{INK}" fill-rule="evenodd" d="'
        "M128 66 A62 62 0 1 1 127.99 66 Z "
        f"M128 {f(top)} A{f(radius)} {f(radius)} 0 0 1 128 {f(bottom)} "
        f"A{f(radius)} {f(radius)} 0 0 1 128 {f(top)} Z\"/>"
    )
    return svg("PoPreyClip concept B: hunter eye", ring + iris)


# ---- C: claw rewind -------------------------------------------------------------
def tapered_arm(x0: float, y0: float, x1: float, y1: float, w0: float, w1: float) -> str:
    """Polygon from a wide base (x0,y0) to a narrow claw tip (x1,y1)."""
    dx, dy = x1 - x0, y1 - y0
    length = math.hypot(dx, dy)
    px, py = -dy / length, dx / length
    pts = [
        (x0 + px * w0 / 2, y0 + py * w0 / 2),
        (x1 + px * w1 / 2, y1 + py * w1 / 2),
        (x1 - px * w1 / 2, y1 - py * w1 / 2),
        (x0 - px * w0 / 2, y0 - py * w0 / 2),
    ]
    return "M" + " L".join(f"{f(x)} {f(y)}" for x, y in pts) + " Z"


def concept_c() -> str:
    parts = []
    for vertex_x in (36, 124):
        vx, vy = vertex_x, 128
        reach = 84
        parts.append(tapered_arm(vx, vy, vx + reach, vy - reach, 40, 6))
        parts.append(tapered_arm(vx, vy, vx + reach, vy + reach, 40, 6))
        # Round the vertex so the two arms meet in one soft knuckle.
        parts.append(f"M{f(vx + 20)} {vy} A20 20 0 1 1 {f(vx - 20)} {vy} A20 20 0 1 1 {f(vx + 20)} {vy} Z")
    body = (f'<path fill="{INK}" stroke="{INK}" stroke-width="4" stroke-linejoin="round" '
            f'd="{" ".join(parts)}"/>')
    return svg("PoPreyClip concept C: claw rewind", body)


# ---- v2 -------------------------------------------------------------------------
# A v2: keep the P's top-left corner closed (it is what makes the letter read). The bowl leaves the stem at the top,
# turns down the right side and comes back along the bottom as an arrow that points at the stem: "go back".
def concept_a_v2() -> str:
    w = 32
    stem = f'<path d="M64 40 V224" stroke="{INK}" stroke-width="{w}" stroke-linecap="round" fill="none"/>'
    bowl = (f'<path d="M64 40 H140 A56 56 0 0 1 140 152 H150" stroke="{INK}" stroke-width="{w}" '
            'stroke-linecap="butt" stroke-linejoin="round" fill="none"/>')
    # Arrowhead on the bottom stroke pointing left; tip leaves a 28-unit gap to the stem's right edge (x=80).
    head = (f'<path d="M108 152 L152 116 L152 188 Z" fill="{INK}" stroke="{INK}" stroke-width="12" '
            'stroke-linejoin="round"/>')
    return svg("PoPreyClip concept A: P rewind", stem + bowl + head)


def claw(base_x: float, base_y: float, tip_x: float, tip_y: float, width: float, bend: float) -> str:
    """Curved claw: crescent from a wide base to a sharp tip. `bend` pushes the curve sideways."""
    dx, dy = tip_x - base_x, tip_y - base_y
    length = math.hypot(dx, dy)
    px, py = -dy / length, dx / length
    ax, ay = base_x + px * width / 2, base_y + py * width / 2
    bx, by = base_x - px * width / 2, base_y - py * width / 2
    mx, my = (base_x + tip_x) / 2 + px * bend, (base_y + tip_y) / 2 + py * bend
    # Outer edge bulges more than the inner edge, so the claw is thick at the base and sharp at the tip.
    c1x, c1y = mx + px * width * 0.35, my + py * width * 0.35
    c2x, c2y = mx - px * width * 0.15, my - py * width * 0.15
    return (f'<path fill="{INK}" d="M{f(ax)} {f(ay)} Q{f(c1x)} {f(c1y)} {f(tip_x)} {f(tip_y)} '
            f'Q{f(c2x)} {f(c2y)} {f(bx)} {f(by)} Z"/>')


def concept_c_v2() -> str:
    parts = []
    for vx in (40, 128):
        vy = 128
        parts.append(claw(vx, vy, vx + 88, vy - 92, 44, -14))
        parts.append(claw(vx, vy, vx + 88, vy + 92, 44, 14))
        parts.append(f'<circle cx="{vx}" cy="{vy}" r="22" fill="{INK}"/>')
    return svg("PoPreyClip concept C: claw rewind", "".join(parts))


# ---- v3: audit fixes ------------------------------------------------------------------
# A: move everything up 4 units so top and bottom margins match (36/36).
def concept_a_v3() -> str:
    return concept_a_v2().replace("<svg ", "<svg ", 1).replace(
        '<path d="M64 40 V224"', '<path transform="translate(0 -4)" d="M64 40 V224"').replace(
        '<path d="M64 40 H140', '<path transform="translate(0 -4)" d="M64 40 H140').replace(
        '<path d="M108 152', '<path transform="translate(0 -4)" d="M108 152')


# C: claws at exactly 45 degrees (equal x/y reach) and centred horizontally (29/29 margins).
def concept_c_v3() -> str:
    parts = []
    reach = 88
    for vx in (51, 139):
        vy = 128
        parts.append(claw(vx, vy, vx + reach, vy - reach, 44, -14))
        parts.append(claw(vx, vy, vx + reach, vy + reach, 44, 14))
        parts.append(f'<circle cx="{vx}" cy="{vy}" r="22" fill="{INK}"/>')
    return svg("PoPreyClip concept C: claw rewind", "".join(parts))


for name, build in {
    "a-prewind-v1": concept_a, "b-huntereye-v1": concept_b, "c-clawrewind-v1": concept_c,
    "a-prewind-v2": concept_a_v2, "c-clawrewind-v2": concept_c_v2,
    "a-prewind-v3": concept_a_v3, "c-clawrewind-v3": concept_c_v3,
}.items():
    (OUT / f"{name}.svg").write_text(build(), encoding="utf-8")
    print("ok", name)
