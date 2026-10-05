"""Rewrite Pro-only Font Awesome codepoints to free equivalents.

Touches only `Icon="xxxx"` values on the three icon controls, and only for
codepoints in the remap table, so anything already present in the free fonts is
left alone. Run `audit_icons.py` afterwards to confirm nothing is left blank.
"""
import re
import sys
from collections import Counter
from pathlib import Path

REPO = Path(r"G:\Max\1_My_proects\Amuse")
APP = REPO / "Amuse.App"

ICON_TAGS = ("FontAwesome", "IconButton", "AddOnButton")
ELEMENT_RE = re.compile(r"<(?P<tag>[A-Za-z0-9_:]+)\b(?P<attrs>[^>]*?)/?>", re.S)
ATTR_RE = re.compile(r"(\w+)=\"([^\"]*)\"")
ICON_ATTR_RE = re.compile(
    r'(?P<prefix>\bIcon=")(?:U\+)?(?P<cp>[0-9a-fA-F]{3,6})(?P<suffix>")')

REMAP = {
    "e0b6": "f544",  # Automation header -> robot
    "e17a": "f1fc",  # Menu_FrameToFrame -> film
    "e1dd": "f126",  # ToolTip_Text_Branch -> code
    "e1e3": "f044",  # Menu_TextInstruct -> pencil
    "e1e7": "f0e6",  # TextConverse / conversation -> comment-dots
    "e228": "f03e",  # Gallery -> image
    "e254": "f0c4",  # VideoTimeline SegmentSplit -> scissors
    "e2ab": "f12a",  # VideoTimeline Clear -> eraser
    "e2b4": "f1f8",  # Delete button -> trash-can
    "e479": "f1b3",  # Components -> cubes
    "e4a2": "f0ec",  # VideoInterpolate -> exchange
    "e6c4": "f0e2",  # VideoTimeline Extend -> arrows-left-right
    "e74c": "f0e7",  # Drag-and-drop target -> arrow-down-to-bracket
    "e76b": "f078",  # VideoTimeline item -> chevron-down
    "e76e": "f078",  # SourceSelect indicator -> chevron-down
    "e83d": "f0c7",  # Save conversation -> bookmark
    "e8c6": "f0c5",  # Save result -> copy
    "f30d": "f00d",  # Remove item -> xmark
    "f30f": "f0eb",  # NewVersion available -> lightbulb
    "f361": "f021",  # Environment Rebuild -> refresh
    "f4a4": "f0ad",  # ImageEdit -> wand-magic-sparkles
    "f4a7": "f3e0",  # Rewind conversation -> rotate-left
    "f78a": "f542",  # ControlNet -> diagram-project
    "f893": "f031",  # Text category -> font
    "f894": "f03e",  # TextToImage/Video/Audio + media placeholder -> image
    "f8c9": "f001",  # TextToMusic -> music
    "f8f2": "f03d",  # VideoTimeline placeholder -> video
}

# Line endings differ per file; preserve whatever the file already uses.
# Text is normalised to \n in memory and converted back on write.
def read(path: Path) -> tuple[str, str, bool]:
    raw = path.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    nl = "\r\n" if b"\r\n" in raw else "\n"
    text = raw.decode("utf-8-sig" if bom else "utf-8")
    return text.replace("\r\n", "\n"), nl, bom


def write(path: Path, text: str, nl: str, bom: bool) -> None:
    if nl == "\r\n":
        text = text.replace("\n", "\r\n")
    data = text.encode("utf-8")
    if bom:
        data = b"\xef\xbb\xbf" + data
    path.write_bytes(data)


def rewrite(text: str) -> tuple[str, int]:
    count = 0

    def sub_attr(am: re.Match) -> str:
        nonlocal count
        key = am.group("cp").lower()
        target = REMAP.get(key)
        if target is None:
            return am.group(0)
        count += 1
        return f"{am.group('prefix')}{target}{am.group('suffix')}"

    def repl(m: re.Match) -> str:
        if m.group("tag").rsplit(":", 1)[-1] not in ICON_TAGS:
            return m.group(0)
        attrs = m.group("attrs")
        if "Icon=" not in attrs:
            return m.group(0)
        new_attrs = ICON_ATTR_RE.sub(sub_attr, attrs)
        if new_attrs == attrs:
            return m.group(0)
        return m.group(0).replace(attrs, new_attrs, 1)

    return ELEMENT_RE.sub(repl, text), count


def main() -> int:
    changed_files = 0
    changed_icons = 0
    per_cp: Counter = Counter()

    for xaml in sorted(APP.rglob("*.xaml")):
        text, nl, bom = read(xaml)
        
        new_text, count = rewrite(text)
        if count:
            write(xaml, new_text, nl, bom)
            changed_files += 1
            changed_icons += count
            print(f"  {xaml.relative_to(REPO)}: {count} icon(s)")

    print(f"\nremapped {changed_icons} icon(s) across {changed_files} file(s)")

    # Verify nothing from the table survives.
    leftover = 0
    for xaml in APP.rglob("*.xaml"):
        text, _, _ = read(xaml)
        for m in ELEMENT_RE.finditer(text):
            if m.group("tag").rsplit(":", 1)[-1] not in ICON_TAGS:
                continue
            for am in ICON_ATTR_RE.finditer(m.group("attrs")):
                if am.group("cp").lower() in REMAP:
                    leftover += 1
                    print(f"UNREMAPED {xaml.name}: Icon={am.group('cp')}")
    return 1 if leftover else 0


if __name__ == "__main__":
    sys.exit(main())
