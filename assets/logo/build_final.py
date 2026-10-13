"""Final "Olho do caçador" (concept B) artwork for the current app.

symbol.svg        black master: lens ring + iris with a vertical slit pupil (all filled paths, no strokes)
app-icon.svg      white symbol on the brand indigo rounded square (sizes 32 px and up)
app-icon-small.svg simplified cut for 16-24 px: no outer ring, larger iris and wider pupil so it stays readable
"""

from pathlib import Path

OUT = Path(__file__).parent / "final"
OUT.mkdir(exist_ok=True)
BRAND = "#4F46E5"


def f(v: float) -> str:
    return f"{v:.2f}".rstrip("0").rstrip(".")


def circle(cx: float, cy: float, r: float) -> str:
    """Full circle as two arcs (one subpath)."""
    return f"M{f(cx + r)} {f(cy)} A{f(r)} {f(r)} 0 1 1 {f(cx - r)} {f(cy)} A{f(r)} {f(r)} 0 1 1 {f(cx + r)} {f(cy)} Z"


def pupil(cx: float, cy: float, half_height: float, half_width: float) -> str:
    """Vertical almond (slit pupil) from two equal arcs."""
    radius = (half_height ** 2 + half_width ** 2) / (2 * half_width)
    top, bottom = cy - half_height, cy + half_height
    return (f"M{f(cx)} {f(top)} A{f(radius)} {f(radius)} 0 0 1 {f(cx)} {f(bottom)} "
            f"A{f(radius)} {f(radius)} 0 0 1 {f(cx)} {f(top)} Z")


def eye(fill: str) -> str:
    """Ring (outer r 114, inner r 90) and iris (r 62) with pupil (112 tall, 36 wide), centred at 128,128."""
    ring = f'<path fill="{fill}" fill-rule="evenodd" d="{circle(128, 128, 114)} {circle(128, 128, 90)}"/>'
    iris = f'<path fill="{fill}" fill-rule="evenodd" d="{circle(128, 128, 62)} {pupil(128, 128, 54, 18)}"/>'
    return ring + iris


def small_eye(fill: str) -> str:
    """16-24 px cut: iris only, larger, with a wider pupil."""
    return f'<path fill="{fill}" fill-rule="evenodd" d="{circle(128, 128, 84)} {pupil(128, 128, 70, 26)}"/>'


def svg(title: str, body: str) -> str:
    return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256" '
            f'role="img" aria-labelledby="title"><title id="title">{title}</title>{body}</svg>\n')


container = f'<rect width="256" height="256" rx="56" fill="{BRAND}"/>'
# Symbol at about 70 % of the container, optically centred.
scaled = '<g transform="translate(128 128) scale(0.7) translate(-128 -128)">'

files = {
    "symbol.svg": svg("PoPreyClip", eye("#111111")),
    "app-icon.svg": svg("PoPreyClip", container + scaled + eye("#FFFFFF") + "</g>"),
    "app-icon-small.svg": svg("PoPreyClip", container + scaled + small_eye("#FFFFFF") + "</g>"),
}
for name, content in files.items():
    (OUT / name).write_text(content, encoding="utf-8")
    print("ok", name)
