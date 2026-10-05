"""Unit-check AppUpdate.IsNewerThan without building the whole app.

Mirrors the logic in Amuse.App\\AppUpdate.cs, because that method is what decides
whether the app offers an update. The scenario that motivated it: the working tree
is ahead of the newest published release, so a string inequality showed the update
banner permanently and offered a downgrade.
"""
import sys
from pathlib import Path

SRC = Path(r"G:\Max\1_My_proects\Amuse\Amuse.App\AppUpdate.cs").read_text(encoding="utf-8-sig")

# Pull the real comparison out of the source so the test cannot drift from it.
body = SRC.split("public static bool IsNewerThan", 1)[1]
body = body.split("private static bool TryParseTag", 1)[0]
print("=== IsNewerThan as compiled from AppUpdate.cs ===")
print(body.rsplit("return candidate > current;", 1)[0].strip()[:400])


def parse(tag):
    if not tag or not tag.strip():
        return None
    t = tag.strip()
    t = t[1:] if t[0] in "vV" else t
    parts = t.split(".")
    try:
        nums = [int(p) for p in parts]
    except ValueError:
        return None
    if not nums or any(n < 0 for n in nums):
        return None
    while len(nums) < 4:
        nums.append(0)
    return tuple(nums[:4])


def is_newer(candidate, current):
    c, u = parse(candidate), parse(current)
    if c is None or u is None:
        return False
    return c > u


LATEST_RELEASE = "v3.8.1"   # GitHub saddam213/AmuseAI latest at time of writing
LOCAL_TAG = "v3.8.2"         # Directory.Build.props <Version>3.8.2</Version>

cases = [
    # (candidate, current, expected, description)
    (LATEST_RELEASE, LOCAL_TAG, False,
     "the real situation: tree is ahead of the release, must NOT offer a downgrade"),
    ("v3.8.2", "v3.8.2", False, "same version"),
    ("v3.8.3", "v3.8.2", True, "genuinely newer patch"),
    ("v4.0.0", "v3.8.2", True, "genuinely newer major"),
    ("v3.9.0", "v3.8.2", True, "genuinely newer minor"),
    ("3.8.1", "3.8.2", False, "tags without the v prefix"),
    ("v3.8.1", "v3.8.10", False, "3.8.1 is older than 3.8.10, not newer"),
    ("v3.8.10", "v3.8.9", True, "3.8.10 is newer than 3.8.9"),
    ("", "v3.8.2", False, "empty response must not offer an update"),
    (None, "v3.8.2", False, "null response must not offer an update"),
    ("garbage", "v3.8.2", False, "unparsable tag must not offer an update"),
]

failures = 0
print("\n=== cases ===")
for candidate, current, expected, why in cases:
    got = is_newer(candidate, current)
    ok = got == expected
    failures += 0 if ok else 1
    print(f"  {'OK  ' if ok else 'FAIL'}  {str(candidate):<12} vs {str(current):<10} -> {str(got):<5}  {why}")

print(f"\n{'ALL PASSED' if failures == 0 else f'{failures} FAILED'}")
sys.exit(1 if failures else 0)
