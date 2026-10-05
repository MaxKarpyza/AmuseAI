"""Audit which icon codepoints used in Amuse XAML exist in the free FontAwesome OTFs.

Usage: python audit_icons.py
"""
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

from fontTools.ttLib import TTFont

REPO = Path(__file__).resolve().parent.parent
APP = REPO / "Amuse.App"
FONT_DIR = REPO / "Amuse.App" / "Fonts" / "Free"

# <CommonControls:FontAwesome Icon="e74c" IconStyle="SharpLight" ... />
ICON_TAGS = ("FontAwesome", "IconButton", "AddOnButton")
ELEMENT_RE = re.compile(
    r"<(?P<tag>[A-Za-z0-9_:]+)\b(?P<attrs>[^>]*?)/?>", re.S)
ATTR_RE = re.compile(r'(\w+)="([^"]*)"')


def is_icon_control(tag: str) -> bool:
    return tag.rsplit(":", 1)[-1] in ICON_TAGS


def load_codepoints(path: Path) -> set:
    font = TTFont(str(path), lazy=True)
    cps = set()
    for table in font["cmap"].tables:
        cps.update(table.cmap.keys())
    name = font["name"]
    family = name.getDebugName(1)
    subfamily = name.getDebugName(2)
    full = name.getDebugName(4)
    font.close()
    return cps, family, subfamily, full


def main() -> int:
    fonts = {}
    for p in sorted(FONT_DIR.glob("*.otf")):
        cps, family, subfamily, full = load_codepoints(p)
        fonts[p.name] = cps
        print(f"{p.name}\n  family(1)  = {family}\n  subfamily(2)= {subfamily}\n"
              f"  full(4)    = {full}\n  codepoints = {len(cps)}")

    # Solid is the widest free coverage; use union for "does it exist at all".
    union = set().union(*fonts.values())
    solid = next(v for k, v in fonts.items() if "Solid" in k)
    regular = next(v for k, v in fonts.items() if "Regular" in k and "Brands" not in k)
    brands = next(v for k, v in fonts.items() if "Brands" in k)

    usage: dict[int, Counter] = defaultdict(Counter)
    files_for: dict[int, set] = defaultdict(set)
    tag_for: dict[int, Counter] = defaultdict(Counter)
    skipped = 0
    for xaml in APP.rglob("*.xaml"):
        text = xaml.read_text(encoding="utf-8", errors="replace")
        for m in ELEMENT_RE.finditer(text):
            if not is_icon_control(m.group("tag")):
                continue
            attrs = dict(ATTR_RE.findall(m.group("attrs")))
            icon = attrs.get("Icon")
            style = attrs.get("IconStyle")
            if icon is None:
                skipped += 1
                continue
            style = style or "(default=SharpLight)"
            try:
                cp = int(icon, 16)
            except ValueError:
                skipped += 1
                continue
            usage[cp][style] += 1
            files_for[cp].add(xaml.name)
            tag_for[cp][m.group("tag").rsplit(":", 1)[-1]] += 1

    print(f"\n{'='*74}\nDistinct codepoints used in XAML: {len(usage)}"
          f"\nTotal icon usages: {sum(sum(c.values()) for c in usage.values())}"
          f"\nSkipped (non-hex / no Icon): {skipped}")

    missing = []
    for cp in sorted(usage):
        if cp not in union:
            missing.append(cp)

    # Degradation plan: Light/Thin/Sharp* are Pro-only styles -> fall back to a free style.
    def targets(style: str) -> list:
        if style in ("Solid", "SharpSolid"):
            return [solid]
        if style in ("Brands",):
            return [brands]
        return [regular, solid]  # Light/Thin/SharpLight/SharpRegular/SharpThin

    style_report = defaultdict(lambda: {"total": 0, "free": 0, "missing": 0})
    for cp, styles in usage.items():
        for style, count in styles.items():
            rep = style_report[style]
            rep["total"] += count
            hit = any(cp in t for t in targets(style))
            rep["free" if hit else "missing"] += count

    print(f"\n{'STYLE':<14}{'USAGES':>9}{'RENDERABLE':>13}{'BLANK':>8}  (free-font fallback)")
    for style, rep in sorted(style_report.items(), key=lambda kv: -kv[1]["total"]):
        pct = 100.0 * rep["free"] / rep["total"]
        print(f"{style:<14}{rep['total']:>9}{rep['free']:>10} ({pct:4.1f}%){rep['missing']:>8}")

    print(f"\n{'='*74}\nCodepoints absent from ALL free fonts (would render blank): {len(missing)}")
    for cp in missing:
        where = ", ".join(sorted(files_for[cp])[:3])
        print(f"  U+{cp:04X}  x{sum(usage[cp].values()):<3} styles={dict(usage[cp])}  {where}")

    total_uses = sum(sum(c.values()) for c in usage.values())
    blank = sum(sum(c.values()) for cp in missing for c in [usage[cp]])
    print(f"\nUsages that stay blank: {blank}/{total_uses} "
          f"({100.0*blank/total_uses:.1f}%)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
