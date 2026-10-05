"""Look up real codepoints in the Font Awesome Free metadata (icons.json).

icons.json is keyed by decimal codepoint; each entry has label, unicode (hex),
styles and search terms. Only codepoints present in the bundled Free Solid face
are offered, so a replacement is guaranteed to render.
"""
import json
import sys
from pathlib import Path

from fontTools.ttLib import TTFont

REPO = Path(r"G:\Max\1_My_proects\Amuse")
META = next((REPO / ".tools" / "fa-free").rglob("icons.json"))
DATA = json.loads(META.read_text(encoding="utf-8"))

solid = TTFont(str(REPO / "Amuse.App" / "Fonts" / "Free" / "FontAwesome-Free-Solid-900.otf"), lazy=True)
HAVE = set()
for t in solid["cmap"].tables:
    HAVE.update(t.cmap.keys())
solid.close()

KEYWORDS = [k.lower() for k in (sys.argv[1:] or [
    "arrow", "wand", "sparkle", "magic", "folder", "grid", "image", "photo", "film",
    "music", "note", "headphone", "microphone", "sliders", "gauge", "bolt", "gear",
    "cog", "text", "font", "quote", "spell", "caption", "shuffle", "list", "palette",
    "brush", "wave", "signal", "bullseye", "compass", "flask", "lightbulb", "layer",
    "stack", "grip", "ellipsis", "rotate", "sync", "repeat", "wand", "paint", "crop",
    "circle-dot", "seed", "dial", "slashed", "project-diagram", "diagram",
])]


def matches(entry: dict) -> bool:
    hay = [entry.get("label", "")] + entry.get("search", {}).get("terms", [])
    blob = " ".join(hay).lower()
    return any(k in blob for k in KEYWORDS)


rows = []
for key, entry in DATA.items():
    if not isinstance(entry, dict) or not matches(entry):
        continue
    hexs = entry.get("unicode", "")
    try:
        cp = int(hexs, 16)
    except (TypeError, ValueError):
        continue
    rows.append((cp not in HAVE, cp, hexs, entry.get("label", ""),
                 ",".join(entry.get("styles", []))))

rows.sort(key=lambda r: (r[0], r[1]))
present = [r for r in rows if not r[0]]

print(f"### present in Free Solid ({len(present)} matches)")
for _, cp, hexs, label, styles in present:
    print(f"  {hexs:<7} U+{cp:04X}  {label:<32} [{styles}]")
