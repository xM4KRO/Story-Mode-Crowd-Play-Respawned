"""Check the new installer payloads for forbidden full game files.

This is a technical content audit, not a legal determination about patches.
"""

from __future__ import annotations

import argparse
import json
import zipfile
from pathlib import Path


COMMON_INSTALL = {
    "dinput8.dll",
    "CrowdPlayHost.exe",
    "cloudflared.exe",
    "CrowdPlayLAN/cloudflared.LICENSE",
    "CrowdPlayLAN/assets/Minecraft.otf",
    "CrowdPlayLAN/assets/Minecraft-OFL.LICENSE",
    "CrowdPlayLAN/assets/Dirt_background_BE2.png",
}


def audit_payload(path: Path, season: str) -> dict[str, object]:
    with zipfile.ZipFile(path) as archive:
        names = {item.filename for item in archive.infolist() if item.filename[-1:] != "/"}
        if len(names) != len([item for item in archive.infolist() if item.filename[-1:] != "/"]):
            raise ValueError(f"duplicate ZIP entries: {path}")
        installs = {name.removeprefix("install/") for name in names if name.startswith("install/")}
        if installs != COMMON_INSTALL:
            raise ValueError(f"unexpected installed payload files in {season}: {installs ^ COMMON_INSTALL}")
        patches = {name for name in names if name.startswith("patches/")}
        if "patches/patch-manifest.json" not in patches:
            raise ValueError(f"missing patch manifest in {season}")
        if names != {"install/" + name for name in installs} | patches:
            raise ValueError(f"unexpected payload path in {season}")
        if any(name.endswith((".ttarch2", ".d3dtx")) for name in names):
            raise ValueError(f"complete game archive/texture found in {season}")
        if any(name.endswith(".png") and name !=
               "install/CrowdPlayLAN/assets/Dirt_background_BE2.png" for name in names):
            raise ValueError(f"unapproved image found in {season}")
        manifest = json.loads(archive.read("patches/patch-manifest.json"))
        if manifest.get("season") != season:
            raise ValueError(f"wrong patch manifest season: {season}")
        return {
            "season": season,
            "archive": str(path),
            "installed_files": len(installs),
            "patch_files": len(patches) - 1,
            "patch_bytes": sum(archive.getinfo(name).file_size for name in patches),
            "full_game_archives": 0,
            "bundled_game_artwork": 0,
        }


def audit_tools(path: Path) -> dict[str, object]:
    with zipfile.ZipFile(path) as archive:
        names = [item.filename for item in archive.infolist() if not item.is_dir()]
        if any(name.endswith((".ttarch2", ".lua", ".d3dtx", ".png", ".otf")) for name in names):
            raise ValueError("game content or artwork found in temporary tools ZIP")
        for required in ("python/python.exe", "python/xdelta3.exe", "python/LICENSE.txt",
                         "licenses/xdelta3.LICENSE", "vendor/ttg-cli-inspector-LICENSE.txt"):
            if required not in names:
                raise ValueError(f"missing tool or license: {required}")
        return {"tools_archive": str(path), "files": len(names)}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--s1", required=True, type=Path)
    parser.add_argument("--s2", required=True, type=Path)
    parser.add_argument("--tools", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    args = parser.parse_args()
    report = {
        "payloads": [audit_payload(args.s1, "s1"), audit_payload(args.s2, "s2")],
        "tools": audit_tools(args.tools),
        "limitation": "Binary deltas may still contain protected expression; seek rights review.",
    }
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
