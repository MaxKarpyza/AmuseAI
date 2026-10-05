"""Verify the Pro-only -> Free icon remap table.

For each entry: confirm the SOURCE codepoint is absent from all free fonts
(so it really must be replaced) and the TARGET exists in the intended free font.
"""
import sys
from pathlib import Path

from fontTools.ttLib import TTFont

REPO = Path(__file__).resolve().parent.parent
FONT_DIR = REPO / "Amuse.App" / "Fonts" / "Free"

# source_hex -> (target_hex, target_font_substring, semantic reason)
REMAP = {
    "e0b6": ("f544", "Solid",        "Automation header -> robot"),
    "e17a": ("f1fc", "Solid",        "Menu_FrameToFrame -> film"),
    "e1dd": ("f126", "Solid",        "ToolTip_Text_Branch -> code"),
    "e1e3": ("f044", "Solid",        "Menu_TextInstruct -> pencil"),
    "e1e7": ("f0e6", "Solid",        "TextConverse / conversation -> comment-dots"),
    "e228": ("f03e", "Solid",        "Gallery -> image"),
    "e254": ("f0c4", "Solid",        "VideoTimeline SegmentSplit -> scissors"),
    "e2ab": ("f12a", "Solid",        "VideoTimeline Clear -> eraser"),
    "e2b4": ("f1f8", "Solid",        "Delete button -> trash-can"),
    "e479": ("f1b3", "Solid",        "Components -> cubes"),
    "e4a2": ("f0ec", "Solid",        "VideoInterpolate -> exchange"),
    "e6c4": ("f0e2", "Solid",        "VideoTimeline Extend -> arrows-left-right"),
    "e74c": ("f0e7", "Solid",        "Drag-and-drop target -> arrow-down-to-bracket"),
    "e76b": ("f078", "Solid",        "VideoTimeline item -> chevron-down"),
    "e76e": ("f078", "Solid",        "SourceSelect indicator -> chevron-down"),
    "e83d": ("f0c7", "Solid",        "Save conversation -> bookmark"),
    "e8c6": ("f0c5", "Solid",        "Save result -> copy"),
    "f30d": ("f00d", "Solid",        "Remove item -> xmark"),
    "f30f": ("f0eb", "Solid",        "NewVersion available -> lightbulb"),
    "f361": ("f021", "Solid",        "Environment Rebuild -> refresh"),
    "f4a4": ("f0ad", "Solid",        "ImageEdit -> wand-magic-sparkles"),
    "f4a7": ("f3e0", "Solid",        "Rewind conversation -> rotate-left"),
    "f78a": ("f542", "Solid",        "ControlNet -> diagram-project"),
    "f893": ("f031", "Solid",        "Text category -> font"),
    "f894": ("f03e", "Solid",        "TextToImage/Video/Audio + media placeholder -> image"),
    "f8c9": ("f001", "Solid",        "TextToMusic -> music"),
    "f8f2": ("f03d", "Solid",        "VideoTimeline placeholder -> video"),
}


def codepoints(path: Path) -> set:
    font = TTFont(str(path), lazy=True)
    cps = set()
    for table in font["cmap"].tables:
        cps.update(table.cmap.keys())
    font.close()
    return cps


def main() -> int:
    fonts = {p.name: codepoints(p) for p in sorted(FONT_DIR.glob("*.otf"))}
    union = set().union(*fonts.values())
    by_kind = {
        "Solid": next(v for k, v in fonts.items() if "Free-Solid" in k),
        "Regular": next(v for k, v in fonts.items() if "Free-Regular" in k),
        "Brands": next(v for k, v in fonts.items() if "Brands" in k),
    }

    print(f"{'SRC':<8}{'TGT':<8}{'KIND':<9}{'SRC-ABSENT':<12}{'TGT-PRESENT':<13}REASON")
    bad = 0
    for src, (tgt, kind, reason) in sorted(REMAP.items()):
        s, t = int(src, 16), int(tgt, 16)
        src_absent = s not in union
        tgt_ok = t in by_kind[kind]
        if not (src_absent and tgt_ok):
            bad += 1
        flag = "" if (src_absent and tgt_ok) else "  <-- CHECK"
        print(f"{src:<8}{tgt:<8}{kind:<9}{str(src_absent):<12}"
              f"{str(tgt_ok):<13}{reason}{flag}")

    print(f"\n{len(REMAP)} entries, {bad} need attention")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
