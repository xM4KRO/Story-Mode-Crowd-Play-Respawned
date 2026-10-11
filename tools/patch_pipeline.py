"""Build and apply the CrowdPlay entry-level TTARCH2 patches.

Only locally decrypted Lua bytecode *entries* are delta-patched. The original
game archives stay untouched. The patched entries are re-encrypted locally and
packed into the same sidecar archives used by the existing mod.
"""

from __future__ import annotations

import hashlib
import json
import os
import struct
import subprocess
import sys
import tempfile
import zlib
from pathlib import Path, PurePosixPath
from typing import Any

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE / "vendor"))

from ttg_cli_inspector.blowfish import BlowfishV7
from ttg_cli_inspector.patches import SidecarDescriptor, decrypt_lenc, encrypt_lenc
from ttg_cli_inspector.profiles import resolve_profile
from ttg_cli_inspector.ttarch2 import Ttarch2Archive, pack_archive


SEASONS = {
    "s1": {
        "profile": "mcsm-season-one",
        "archive_prefix": "MCSM_pc_",
        "original": {
            "Project": "MCSM_pc_Project_data.ttarch2",
            "Menu": "MCSM_pc_Menu_data.ttarch2",
        },
        "mod_only": {"Project": {"CrowdPlay.lua", "CrowdPlayOverlay.lua"}, "Menu": set()},
    },
    "s2": {
        "profile": "mcsm-season-two",
        "archive_prefix": "MC2_pc_",
        "original": {
            "Project": "MC2_pc_Project_data.ttarch2",
            "Menu": "MC2_pc_Menu_data.ttarch2",
        },
        "mod_only": {"Project": set(), "Menu": set()},
    },
}

CURSOR = {
    "s1": {
        "archive": "Archives/MCSM_pc_Project_txmesh.ttarch2",
        "entry": "ui_cursor_default.d3dtx",
        "base_sha256": "25d86f2cf34436fe1f93fc2221bec0e484d5ead4965b03a75a0a679a99e141ff",
        "texture_offset": 272,
        "recolor": False,
    },
    "s2": {
        "archive": "Archives/MC2_pc_Project_txmesh.ttarch2",
        "entry": "ui_cursor_highlight.d3dtx",
        "base_sha256": "8f63ae3036fea50bf9e7bcf643eda627db053b969d24ace97a3ad8c272f60126",
        "texture_offset": 310,
        "recolor": True,
    },
}
CURSOR_OUTPUT = "CrowdPlayLAN/assets/StoryModeCursor.png"
# This is the SHA-256 of decoded RGBA pixels, not a copy of the PNG asset.
CURSOR_PIXELS_SHA256 = "c4ba76e23d0b9dfe2daf61c084ae73e07a7c74e5b994d9ca983d7287cc5d548c"
CURSOR_PNG_SHA256 = "c5de5853a970b28be0daa3723c3e892e2ee4b47df0692a7d8a4e9df4eb9795df"


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def hash_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def safe_relative(path: str) -> Path:
    pure = PurePosixPath(path)
    if not path or pure.is_absolute() or not pure.parts or any(
        part in {"", ".", ".."} or ":" in part or "\\" in part for part in pure.parts
    ):
        raise ValueError(f"unsafe relative path: {path!r}")
    return Path(*pure.parts)


def checked_path(root: Path, relative: str) -> Path:
    result = root / safe_relative(relative)
    # The extracted installer payload is private temporary data. Still reject
    # a link that could redirect a manifest path outside its intended root.
    current = root
    for part in safe_relative(relative).parts:
        current = current / part
        if current.is_symlink():
            raise ValueError(f"symlink in patch path: {current}")
    return result


def entry_bytes(archive: Ttarch2Archive, name: str) -> bytes:
    matches = [entry for entry in archive.files if entry.name == name]
    if len(matches) != 1:
        raise ValueError(f"expected one {name!r} entry, found {len(matches)}")
    return archive.read_file(matches[0])


def decode_stock_lua(raw: bytes, profile: str) -> bytes:
    """Unwrap either stock LEn or LEo bytecode without writing game files."""
    if raw.startswith(b"\x1bLEn"):
        selected = resolve_profile(profile)
        return b"\x1bLua" + BlowfishV7(selected.key).decrypt_ecb(raw[4:])
    if raw.startswith(b"\x1bLEo"):
        return decrypt_lenc(raw, profile)
    raise ValueError("stock Lua entry has an unrecognized LENC wrapper")


def run_xdelta(xdelta: Path, operation: str, base: bytes, value: bytes) -> bytes:
    """Run a pinned xdelta3 executable without showing a console on Windows."""
    if operation not in {"encode", "decode"}:
        raise ValueError(operation)
    if not xdelta.is_file():
        raise FileNotFoundError(f"xdelta3 was not found: {xdelta}")
    with tempfile.TemporaryDirectory(prefix="crowdplay-xdelta-") as folder:
        temp = Path(folder)
        old_path, input_path, output_path = temp / "base", temp / "input", temp / "output"
        old_path.write_bytes(base)
        input_path.write_bytes(value)
        cmd = [str(xdelta), "-f"]
        if operation == "encode":
            # xdelta otherwise stores temporary absolute file paths in its
            # application header, leaking the developer's machine username.
            cmd.extend(["-A", "", "-e"])
        else:
            cmd.append("-d")
        cmd.extend(["-s", str(old_path), str(input_path), str(output_path)])
        kwargs: dict[str, Any] = {}
        if os.name == "nt":
            kwargs["creationflags"] = subprocess.CREATE_NO_WINDOW
        result = subprocess.run(cmd, stdin=subprocess.DEVNULL, capture_output=True,
                                timeout=120, **kwargs)
        if result.returncode != 0:
            message = result.stderr.decode("utf-8", "replace").strip()
            raise RuntimeError(f"xdelta3 {operation} failed: {message}")
        if not output_path.is_file():
            raise RuntimeError(f"xdelta3 {operation} produced no output")
        return output_path.read_bytes()


def sidecar_name(season: str, group: str) -> str:
    return f"{SEASONS[season]['archive_prefix']}CrowdPlayLAN_{group}.ttarch2"


def descriptor_bytes(season: str, group: str) -> bytes:
    name = f"CrowdPlayLAN_{group}"
    archive_name = sidecar_name(season, group)
    descriptor_name = f"_resdesc_50_{name}.lua"
    descriptor = SidecarDescriptor(
        name=name, set_name=name, logical_name=name, destination=group,
        archive_name=archive_name, descriptor_name=descriptor_name,
        priority=1100, enable_mode="constant",
    )
    description = descriptor.render().replace(
        "set.gameDataPriority = 0", "set.gameDataPriority = 1100"
    )
    return encrypt_lenc(description.encode("utf-8"), SEASONS[season]["profile"])


def generate_season(
    season: str, game_root: Path, build_root: Path, output_root: Path, xdelta: Path
) -> dict[str, Any]:
    if season not in SEASONS:
        raise ValueError(f"unknown season: {season}")
    spec = SEASONS[season]
    payload_root = output_root / season
    patches_root = payload_root / "patches"
    patches_root.mkdir(parents=True, exist_ok=True)
    manifest: dict[str, Any] = {"schema": 1, "season": season, "archives": []}

    for group in ("Project", "Menu"):
        original_relative = f"Archives/{spec['original'][group]}"
        original_path = checked_path(game_root, original_relative)
        if not original_path.is_file():
            raise FileNotFoundError(original_path)
        target_name = sidecar_name(season, group)
        target_path = build_root / season / "Archives" / target_name
        if not target_path.is_file():
            raise FileNotFoundError(target_path)

        original = Ttarch2Archive(original_path, spec["profile"])
        target = Ttarch2Archive(target_path, spec["profile"])
        listed: list[dict[str, Any]] = []
        mod_only_seen: set[str] = set()
        for entry in target.files:
            name = entry.name
            if "/" in name or "\\" in name or not name.endswith(".lua"):
                raise ValueError(f"unexpected sidecar entry: {name}")
            modified_bytes = target.read_file(entry)
            if name in spec["mod_only"][group]:
                mod_only_seen.add(name)
                blob_relative = f"patches/{group}_{name}.modentry"
                blob_path = checked_path(payload_root, blob_relative)
                blob_path.write_bytes(modified_bytes)
                listed.append({
                    "name": name, "method": "mod-only", "blob": blob_relative,
                    "blob_sha256": sha256(modified_bytes),
                    "result_sha256": sha256(modified_bytes),
                })
                continue

            base_bytes = entry_bytes(original, name)
            base_plain = decode_stock_lua(base_bytes, spec["profile"])
            modified_plain = decrypt_lenc(modified_bytes, spec["profile"])
            patch_relative = f"patches/{group}_{name}.patch"
            patch_path = checked_path(payload_root, patch_relative)
            delta = run_xdelta(xdelta, "encode", base_plain, modified_plain)
            if run_xdelta(xdelta, "decode", base_plain, delta) != modified_plain:
                raise RuntimeError(f"delta round-trip failed: {season}/{group}/{name}")
            if encrypt_lenc(modified_plain, spec["profile"]) != modified_bytes:
                raise RuntimeError(f"target LENC round-trip failed: {season}/{group}/{name}")
            patch_path.write_bytes(delta)
            listed.append({
                "name": name, "method": "xdelta3", "patch": patch_relative,
                "base_sha256": sha256(base_bytes),
                "base_plain_sha256": sha256(base_plain),
                "patch_sha256": sha256(delta),
                "result_plain_sha256": sha256(modified_plain),
                "result_sha256": sha256(modified_bytes),
            })

        if mod_only_seen != spec["mod_only"][group]:
            raise ValueError(f"missing mod-only entries: {spec['mod_only'][group] - mod_only_seen}")
        listed.sort(key=lambda item: item["name"])
        manifest["archives"].append({
            "group": group,
            "original": original_relative,
            "original_sha256": hash_file(original_path),
            "output": f"Archives/{target_name}",
            "output_sha256": hash_file(target_path),
            "descriptor": f"Archives/_resdesc_50_CrowdPlayLAN_{group}.lua",
            "descriptor_sha256": sha256(descriptor_bytes(season, group)),
            "entries": listed,
        })
        descriptor_path = target_path.with_name(f"_resdesc_50_CrowdPlayLAN_{group}.lua")
        if descriptor_path.read_bytes() != descriptor_bytes(season, group):
            raise RuntimeError(f"descriptor generation differs: {season}/{group}")

    manifest["cursor"] = {
        **CURSOR[season], "output": CURSOR_OUTPUT,
        "pixels_sha256": CURSOR_PIXELS_SHA256,
        "output_sha256": CURSOR_PNG_SHA256,
    }
    manifest_path = patches_root / "patch-manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n",
                             encoding="utf-8", newline="\n")
    return manifest


def _rgb565(value: int) -> tuple[int, int, int]:
    r, g, b = (value >> 11) & 31, (value >> 5) & 63, value & 31
    return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2))


def decode_bc3(data: bytes, width: int, height: int) -> bytes:
    """Decode the stock little-endian BC3 cursor texture with the stdlib."""
    if width % 4 or height % 4 or len(data) != width * height:
        raise ValueError("unsupported BC3 cursor dimensions or payload size")
    rgba = bytearray(width * height * 4)
    for block_y in range(height // 4):
        for block_x in range(width // 4):
            start = (block_y * (width // 4) + block_x) * 16
            block = data[start:start + 16]
            a0, a1 = block[0], block[1]
            alpha_bits = int.from_bytes(block[2:8], "little")
            if a0 > a1:
                alphas = [a0, a1] + [((7 - i) * a0 + i * a1) // 7 for i in range(1, 7)]
            else:
                alphas = [a0, a1] + [((5 - i) * a0 + i * a1) // 5 for i in range(1, 5)] + [0, 255]
            c0, c1 = struct.unpack_from("<HH", block, 8)
            rgb0, rgb1 = _rgb565(c0), _rgb565(c1)
            colors = [rgb0, rgb1,
                      tuple((2 * x + y) // 3 for x, y in zip(rgb0, rgb1)),
                      tuple((x + 2 * y) // 3 for x, y in zip(rgb0, rgb1))]
            color_bits = int.from_bytes(block[12:16], "little")
            for pixel in range(16):
                x = block_x * 4 + pixel % 4
                y = block_y * 4 + pixel // 4
                dest = (y * width + x) * 4
                rgba[dest:dest + 3] = bytes(colors[(color_bits >> (pixel * 2)) & 3])
                rgba[dest + 3] = alphas[(alpha_bits >> (pixel * 3)) & 7]
    return bytes(rgba)


def png_rgba(width: int, height: int, pixels: bytes) -> bytes:
    """Encode a simple, standards-compliant PNG without embedding an image."""
    if len(pixels) != width * height * 4:
        raise ValueError("invalid RGBA pixel count")
    rows = b"".join(b"\0" + pixels[y * width * 4:(y + 1) * width * 4]
                    for y in range(height))

    def chunk(tag: bytes, payload: bytes) -> bytes:
        return struct.pack(">I", len(payload)) + tag + payload + struct.pack(
            ">I", zlib.crc32(tag + payload) & 0xFFFFFFFF
        )

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows, level=9))
            + chunk(b"IEND", b""))


def reconstruct_cursor(season: str, game_root: Path, output_root: Path) -> Path:
    source_spec = CURSOR[season]
    source = checked_path(game_root, source_spec["archive"])
    archive = Ttarch2Archive(source, SEASONS[season]["profile"])
    raw = entry_bytes(archive, source_spec["entry"])
    if sha256(raw) != source_spec["base_sha256"]:
        raise ValueError(f"unsupported {season} cursor archive entry")
    offset = source_spec["texture_offset"]
    if len(raw) != offset + 4096:
        raise ValueError(f"unsupported {season} cursor texture format")
    source_pixels = decode_bc3(raw[offset:], 64, 64)
    pixels = bytearray(32 * 32 * 4)
    for y in range(32):
        for x in range(32):
            src = ((y * 2 + 1) * 64 + x * 2 + 1) * 4
            dst = (y * 32 + x) * 4
            pixel = source_pixels[src:src + 4]
            if source_spec["recolor"] and pixel == b"\xff\xff\x99\xff":
                pixel = b"\xff\xff\xff\xff"
            pixels[dst:dst + 4] = pixel
    if sha256(pixels) != CURSOR_PIXELS_SHA256:
        raise RuntimeError("derived cursor pixels did not match the tested build")
    target = checked_path(output_root, CURSOR_OUTPUT)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(png_rgba(32, 32, pixels))
    if hash_file(target) != CURSOR_PNG_SHA256:
        raise RuntimeError("derived cursor PNG did not match the tested build")
    return target


def apply_season(
    season: str, game_root: Path, payload_root: Path, output_root: Path,
    xdelta: Path,
) -> list[Path]:
    manifest_path = checked_path(payload_root, "patches/patch-manifest.json")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if manifest.get("schema") != 1 or manifest.get("season") != season:
        raise ValueError("wrong or unsupported CrowdPlay patch manifest")
    output_files: list[Path] = []
    for group in manifest["archives"]:
        source = checked_path(game_root, group["original"])
        if hash_file(source) != group["original_sha256"]:
            raise ValueError(f"unsupported or changed original archive: {group['original']}")
        original = Ttarch2Archive(source, SEASONS[season]["profile"])
        with tempfile.TemporaryDirectory(prefix="crowdplay-sidecar-") as folder:
            staging = Path(folder)
            for item in group["entries"]:
                name = item["name"]
                if Path(name).name != name or not name.endswith(".lua"):
                    raise ValueError(f"unsafe sidecar entry: {name}")
                if item["method"] == "mod-only":
                    result = checked_path(payload_root, item["blob"]).read_bytes()
                    if sha256(result) != item["blob_sha256"]:
                        raise ValueError(f"mod entry hash mismatch: {name}")
                elif item["method"] == "xdelta3":
                    base = entry_bytes(original, name)
                    if sha256(base) != item["base_sha256"]:
                        raise ValueError(f"wrong base entry: {name}")
                    base_plain = decode_stock_lua(base, SEASONS[season]["profile"])
                    if sha256(base_plain) != item["base_plain_sha256"]:
                        raise ValueError(f"wrong decoded base entry: {name}")
                    delta = checked_path(payload_root, item["patch"]).read_bytes()
                    if sha256(delta) != item["patch_sha256"]:
                        raise ValueError(f"patch hash mismatch: {name}")
                    result_plain = run_xdelta(xdelta, "decode", base_plain, delta)
                    if sha256(result_plain) != item["result_plain_sha256"]:
                        raise ValueError(f"decoded target entry hash mismatch: {name}")
                    result = encrypt_lenc(result_plain, SEASONS[season]["profile"])
                else:
                    raise ValueError(f"unsupported patch method: {item['method']}")
                if sha256(result) != item["result_sha256"]:
                    raise ValueError(f"patched entry hash mismatch: {name}")
                (staging / name).write_bytes(result)

            output = checked_path(output_root, group["output"])
            output.parent.mkdir(parents=True, exist_ok=True)
            pack_archive(staging, output)
            if hash_file(output) != group["output_sha256"]:
                raise RuntimeError(f"sidecar reconstruction differed: {group['output']}")
            output_files.append(output)

            descriptor = checked_path(output_root, group["descriptor"])
            descriptor.write_bytes(descriptor_bytes(season, group["group"]))
            if hash_file(descriptor) != group["descriptor_sha256"]:
                raise RuntimeError(f"descriptor reconstruction differed: {group['descriptor']}")
            output_files.append(descriptor)

    expected_cursor = {
        **CURSOR[season], "output": CURSOR_OUTPUT,
        "pixels_sha256": CURSOR_PIXELS_SHA256,
        "output_sha256": CURSOR_PNG_SHA256,
    }
    if manifest.get("cursor") != expected_cursor:
        raise ValueError("cursor source manifest was changed")
    output_files.append(reconstruct_cursor(season, game_root, output_root))
    return output_files
