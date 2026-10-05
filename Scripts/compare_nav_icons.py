"""Compare nav icon usage in MainWindow.xaml before and after the icon remap.

Finds labels that lost their distinct icon, i.e. collisions the remap introduced.
"""
import os
import re
import subprocess
from collections import defaultdict
from pathlib import Path

REPO = Path(r"G:\Max\1_My_proects\Amuse")
REL = "Amuse.App/MainWindow.xaml"


def scan(text: str) -> dict:
    items = {}
    cur = None
    for ln in text.splitlines():
        m = re.search(r"Localize (\w+)\}", ln)
        if m:
            cur = m.group(1)
        m = re.search(r'\bIcon="([0-9a-fA-F]{3,6})"', ln)
        if m and cur:
            items[cur] = m.group(1)
            cur = None
    return items


def group(items: dict) -> dict:
    out = defaultdict(list)
    for label, icon in items.items():
        out[icon].append(label)
    return {i: l for i, l in out.items() if len(l) > 1}


orig_text = subprocess.run(
    ["git", "-C", str(REPO), "show", f"HEAD:{REL}"],
    capture_output=True, check=True).stdout.decode("utf-8-sig")
new_text = (REPO / REL).read_text(encoding="utf-8-sig")

orig, new = scan(orig_text), scan(new_text)
go, gn = group(orig), group(new)

print(f"nav items: original {len(orig)}, now {len(new)}\n")
print("=== ORIGINAL: one icon shared by several labels ===")
for icon, labels in sorted(go.items()):
    print(f"  {icon}: {labels}")
print("\n=== NOW: one icon shared by several labels ===")
for icon, labels in sorted(gn.items()):
    print(f"  {icon}: {labels}")

print("\n=== labels whose icon changed, and who they now collide with ===")
regressions = []
for label in sorted(orig):
    if label not in new or orig[label] == new[label]:
        continue
    peers = [l for l in new if l != label and new[l] == new[label]]
    status = "COLLISION -> " + ", ".join(sorted(peers)) if peers else "ok"
    print(f"  {label:<26} {orig[label]} -> {new[label]}   {status}")
    if peers:
        regressions.append((label, orig[label], new[label], peers))

print(f"\n{len(regressions)} label(s) now share an icon they did not before:")
for label, o, n, peers in regressions:
    print(f"  {label}: {o} -> {n} (also used by {', '.join(sorted(peers))})")
