"""Verify Fonts.Free.xaml end to end: parse the real style -> font mapping, then
check that every (style, codepoint) pair used in the XAML has a glyph in the font
that style actually points at. This mirrors runtime resolution.

Also reports the size of any gap, so the fallback table can be tuned.
"""
import re
import sys
from collections import defaultdict
from pathlib import Path

from fontTools.ttLib import TTFont

REPO = Path(r"G:\Max\1_My_proects\Amuse")
APP = REPO / "Amuse.App"
FONTS = APP / "Fonts" / "Free"
DICT = APP / "Fonts" / "Fonts.Free.xaml"

ICON_TAGS = ("FontAwesome", "IconButton", "AddOnButton")
ELEMENT_RE = re.compile(r"<(?P<tag>[A-Za-z0-9_:]+)\b(?P<attrs>[^>]*?)/?>", re.S)
KEY_RE = re.compile(r'x:Key="FontAwesome(?P<style>\w+)"')
FAMILY_RE = re.compile(r'>(?P<uri>[^<]*\.otf)#[^<]*<')
DEFAULT_STYLE = "SharpLight"  # FontAwesome control's IconStyle default


def load_mapping() -> dict:
    text = DICT.read_text(encoding="utf-8")
    mapping = {}
    for m in re.finditer(
        r'<FontFamily\s+x:Key="FontAwesome(?P<style>\w+)"\s*>(?P<uri>[^<]+)<', text):
        uri = m.group("uri")
        file = uri.split("#", 1)[0].rsplit("/", 1)[-1]
        mapping[m.group("style")] = file
    return mapping


def load_codepoints(path: Path) -> set:
    font = TTFont(str(path), lazy=True)
    cps = set()
    for table in font["cmap"].tables:
        cps.update(table.cmap.keys())
    font.close()
    return cps


def main() -> int:
    mapping = load_mapping()
    print("=== Fonts.Free.xaml style -> font ===")
    for style, file in sorted(mapping.items()):
        print(f"  {style:<14} -> {file}")

    cps_by_file = {p.name: load_codepoints(p) for p in sorted(FONTS.glob("*.otf"))}

    # (style, codepoint) usages
    usage = defaultdict(lambda: defaultdict(int))
    for xaml in APP.rglob("*.xaml"):
        text = xaml.read_text(encoding="utf-8-sig")
        for m in ELEMENT_RE.finditer(text):
            if m.group("tag").rsplit(":", 1)[-1] not in ICON_TAGS:
                continue
            attrs = m.group("attrs")
            icon = re.search(r'\bIcon="(?:U\+)?([0-9a-fA-F]{3,6})"', attrs)
            if not icon:
                continue
            style = re.search(r'\bIconStyle="(\w+)"', attrs)
            style = style.group(1) if style else DEFAULT_STYLE
            usage[style][int(icon.group(1), 16)] += 1

    print(f"\n{'STYLE':<14}{'FONT':<34}{'USES':>6}{'OK':>6}{'GAP':>6}   MISSING CODEPOINTS")
    total_uses = total_ok = 0
    gaps: dict[str, set] = {}
    for style in sorted(usage, key=lambda s: -sum(usage[s].values())):
        file = mapping.get(style)
        if file is None:
            print(f"  style '{style}' has no entry in Fonts.Free.xaml")
            return 1
        available = cps_by_file[file]
        missing = sorted(cp for cp in usage[style] if cp not in available)
        uses = sum(usage[style].values())
        ok = uses - sum(usage[style][cp] for cp in missing)
        total_uses += uses
        total_ok += ok
        if missing:
            gaps[style] = set(missing)
        print(f"  {style:<14}{file:<34}{uses:>6}{ok:>6}{uses - ok:>6}   " +
              " ".join(f"U+{cp:04X}" for cp in missing[:10]) +
              (" ..." if len(missing) > 10 else ""))

    print(f"\n{total_ok}/{total_uses} icon usages resolve ({100.0*total_ok/total_uses:.1f}%)")
    if gaps:
        # Which codepoints are missing from Regular but available in Solid?
        # Those styles could be repointed at Solid, or the codepoint swapped.
        solid = cps_by_file.get("FontAwesome-Free-Solid-900.otf", set())
        print("\ncodepoints missing from the mapped font but present in Free Solid:")
        for style, missing in sorted(gaps.items()):
            recoverable = sorted(cp for cp in missing if cp in solid)
            lost = sorted(cp for cp in missing if cp not in solid)
            print(f"  {style}: solid has {len(recoverable)}, still lost {len(lost)}"
                  + (f" -> {', '.join(f'U+{cp:04X}' for cp in lost)}" if lost else ""))
        return 1
    print("every usage resolves - no fallback gaps")
    return 0


if __name__ == "__main__":
    sys.exit(main())
