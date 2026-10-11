"""Build the isolated CrowdPlay installer from local patches and licensed code.

The private modified sidecar archives are read only by the patch generator.
They are never copied into an installer payload. The two project-created
voting banners are embedded directly in the finished installer.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parent
BUILD = ROOT / "build"
ASSETS = ROOT / "installer" / "assets"
CLOUDFLARED_SHA256 = "9b95ddc2eba67b86ed3dc4cc2a15881960563031b52ce564376af41fb91ad402"
FONT_SHA256 = "ebc7a7be9f69479f02875af1fe79a2b93cc868b83be4f6369ecceb45fabe5ee6"
S1_BANNER_SHA256 = "3166b641257dab0e2a42537e9956f1e8934ee736d6bccfe47fde80fea60bb658"
S2_BANNER_SHA256 = "56fe183a7e43fbbea74d2f14bc9c2ffa8085d4af14ff79bd7d5da6da8164103f"


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def require_hash(path: Path, expected: str) -> None:
    if not path.is_file() or digest(path) != expected:
        raise ValueError(f"Missing or changed input: {path}")


def run(*parts: object) -> None:
    environment = os.environ.copy()
    environment["PYTHONDONTWRITEBYTECODE"] = "1"
    subprocess.run([str(part) for part in parts], check=True, env=environment)


def add_file(archive: zipfile.ZipFile, source: Path, name: str) -> None:
    if not source.is_file():
        raise FileNotFoundError(source)
    item = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
    item.compress_type = zipfile.ZIP_DEFLATED
    item.external_attr = 0o644 << 16
    archive.writestr(item, source.read_bytes())


def season_payload(season: str, patch_root: Path, host: Path) -> Path:
    target = BUILD / f"{season}-payload.zip"
    with zipfile.ZipFile(target, "w") as archive:
        files = {
            "dinput8.dll": ROOT / "proxy" / ("dinput8_x86.dll" if season == "s1" else "dinput8_x64.dll"),
            "CrowdPlayHost.exe": host,
            "cloudflared.exe": ROOT / "third_party" / "cloudflared" / "cloudflared-windows-386.exe",
            "CrowdPlayLAN/cloudflared.LICENSE": ROOT / "third_party" / "cloudflared" / "LICENSE",
            "CrowdPlayLAN/assets/Minecraft.otf": ASSETS / "Minecraft.otf",
            "CrowdPlayLAN/assets/Minecraft-OFL.LICENSE": ASSETS / "Minecraft-OFL.LICENSE",
            "CrowdPlayLAN/assets/Dirt_background_BE2.png": ASSETS / "Dirt_background_BE2.png",
        }
        for name, source in files.items():
            add_file(archive, source, f"install/{name}")
        patches = patch_root / season / "patches"
        if not (patches / "patch-manifest.json").is_file():
            raise FileNotFoundError(patches / "patch-manifest.json")
        for path in sorted(patches.iterdir()):
            if path.is_file():
                add_file(archive, path, f"patches/{path.name}")
    return target


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--season1-game-root", required=True, type=Path)
    parser.add_argument("--season2-game-root", required=True, type=Path)
    parser.add_argument("--modified-build-root", required=True, type=Path,
                        help="Private existing build directory with s1/Archives and s2/Archives")
    parser.add_argument("--python-embed", required=True, type=Path)
    parser.add_argument("--output", type=Path, default=ROOT / "CrowdPlay-Respawned.exe")
    args = parser.parse_args()

    BUILD.mkdir(parents=True, exist_ok=True)
    cloudflared = ROOT / "third_party" / "cloudflared" / "cloudflared-windows-386.exe"
    require_hash(cloudflared, CLOUDFLARED_SHA256)
    require_hash(ASSETS / "Minecraft.otf", FONT_SHA256)
    require_hash(ASSETS / "S1_Banner.png", S1_BANNER_SHA256)
    require_hash(ASSETS / "S2_Banner.png", S2_BANNER_SHA256)
    xdelta = ROOT / "tools" / "xdelta3" / "xdelta3.exe"
    xdelta_license = ROOT / "tools" / "xdelta3" / "LICENSE"
    patch_root = BUILD / "patches"
    run(sys.executable, ROOT / "tools" / "generate_patches.py",
        "--season1-game-root", args.season1_game_root,
        "--season2-game-root", args.season2_game_root,
        "--build-root", args.modified_build_root,
        "--output-dir", patch_root, "--xdelta", xdelta)
    tools_zip = BUILD / "patch-tools.zip"
    run(sys.executable, ROOT / "tools" / "build_tools_zip.py",
        "--python-embed", args.python_embed,
        "--xdelta", xdelta,
        "--xdelta-license", xdelta_license,
        "--output", tools_zip)
    run("powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
        ROOT / "installer" / "BuildHost.ps1", "-OutputDirectory", BUILD / "host")
    host = BUILD / "host" / "CrowdPlayHost.exe"
    season1_zip = season_payload("s1", patch_root, host)
    season2_zip = season_payload("s2", patch_root, host)
    run(sys.executable, ROOT / "tools" / "audit_payloads.py",
        "--s1", season1_zip, "--s2", season2_zip, "--tools", tools_zip,
        "--report", BUILD / "payload-audit.json")
    run("powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
        ROOT / "installer" / "Build.ps1",
        "-Season1PayloadZip", season1_zip,
        "-Season2PayloadZip", season2_zip,
        "-PatchToolsZip", tools_zip,
        "-OutputDirectory", BUILD / "installer")
    installer = BUILD / "installer" / "InstallCrowdPlay.exe"
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(installer.read_bytes())
    report = {
        "installer": str(args.output),
        "sha256": digest(args.output),
        "bytes": args.output.stat().st_size,
        "season1_payload_sha256": digest(season1_zip),
        "season2_payload_sha256": digest(season2_zip),
        "tools_sha256": digest(tools_zip),
        "season1_banner_sha256": digest(ASSETS / "S1_Banner.png"),
        "season2_banner_sha256": digest(ASSETS / "S2_Banner.png"),
    }
    (BUILD / "build-report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
