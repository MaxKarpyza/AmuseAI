"""Give the bundled Font Awesome Free faces unique family names.

Why this is needed
------------------
WPF reads the typographic family (name ID 16) when it resolves a font family name,
falling back to name ID 1. In the stock Font Awesome Free 7.3.1 files both
`Free-Solid-900.otf` and `Free-Regular-400.otf` declare name ID 16 = "Font Awesome 7 Free",
so WPF cannot tell the two faces apart and the icons silently fall back to a system font.

Font Awesome Free is CC BY 4.0, which permits modification and redistribution provided
the licence and attribution travel with it (Fonts/Free/LICENSE.txt). Setting name ID 16
to the file's own unique name keeps every face distinguishable and still accurate -
the names are not invented, they are the names Font Awesome already uses for these faces.

Idempotent: running it twice produces the same result.
"""
import sys
from pathlib import Path

from fontTools.ttLib import TTFont

FONT_DIR = Path(r"G:\Max\1_My_proects\Amuse\Amuse.App\Fonts\Free")

# file name -> (unique typographic family, subfamily)
TARGETS = {
    "FontAwesome-Free-Solid-900.otf": ("Font Awesome 7 Free Solid", "Regular"),
    "FontAwesome-Free-Regular-400.otf": ("Font Awesome 7 Free Regular", "Regular"),
    "FontAwesome-Free-Brands-Regular-400.otf": ("Font Awesome 7 Brands Regular", "Regular"),
}


def main() -> int:
    problems = 0
    for filename, (family, subfamily) in TARGETS.items():
        path = FONT_DIR / filename
        if not path.exists():
            print(f"  MISSING {filename}")
            problems += 1
            continue

        font = TTFont(str(path))
        name = font["name"]

        before16 = name.getDebugName(16)
        before1 = name.getDebugName(1)

        # name IDs 16/17 (typographic family/subfamily) - what WPF actually reads
        name.setName(family, 16, 3, 1, 0x409)
        name.setName(family, 16, 1, 0, 0)
        name.setName(subfamily, 17, 3, 1, 0x409)
        name.setName(subfamily, 17, 1, 0, 0)

        # name IDs 1/2: only touch ID 1 when it is ambiguous, i.e. equal to another
        # face's typographic family. Solid already has a unique ID 1.
        if before1 == "Font Awesome 7 Free":
            name.setName(family, 1, 3, 1, 0x409)
            name.setName(family, 1, 1, 0, 0)

        font.save(str(path))
        font.close()

        check = TTFont(str(path), lazy=True)
        after16 = check["name"].getDebugName(16)
        after1 = check["name"].getDebugName(1)
        after4 = check["name"].getDebugName(4)
        check.close()

        ok = after16 == family
        problems += 0 if ok else 1
        print(f"  {'OK  ' if ok else 'FAIL'} {filename}")
        print(f"        id16 {before16!r} -> {after16!r}")
        print(f"        id1  {before1!r} -> {after1!r}")
        print(f"        id4  {after4!r}")

    # Uniqueness check across the whole set.
    seen: dict[str, str] = {}
    print("\nuniqueness of name ID 16:")
    for filename in TARGETS:
        f = TTFont(str(FONT_DIR / filename), lazy=True)
        n = f["name"].getDebugName(16)
        f.close()
        clash = seen.get(n)
        seen[n] = filename
        status = "OK  " if clash is None else "CLASH"
        problems += 0 if clash is None else 1
        print(f"  {status} {n!r} <- {filename}")

    print(f"\n{'all good' if problems == 0 else f'{problems} problem(s)'}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
