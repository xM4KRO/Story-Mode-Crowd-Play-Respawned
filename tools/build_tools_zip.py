"""Pack the verified local patching tools into one installer resource.

The official Python embeddable distribution and xdelta3 are redistributed
with their own licenses. This ZIP is temporary installation machinery; none
of these files is left in the game directory.
"""

from __future__ import annotations

import argparse
import hashlib
import zipfile
from pathlib import Path


PYTHON_SHA256 = "4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3"
XDELTA_SHA256 = "6e812b38484d0c764291779fadee83ffbe2eceaccb4e3a21f8a043a02654a01e"


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def add(archive: zipfile.ZipFile, name: str, data: bytes) -> None:
    if name.startswith("/") or ".." in Path(name).parts or "\\" in name:
        raise ValueError(f"unsafe package path: {name}")
    entry = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
    entry.compress_type = zipfile.ZIP_DEFLATED
    entry.external_attr = 0o644 << 16
    archive.writestr(entry, data)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--python-embed", required=True, type=Path)
    parser.add_argument("--xdelta", required=True, type=Path)
    parser.add_argument("--xdelta-license", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if digest(args.python_embed) != PYTHON_SHA256:
        raise ValueError("Python embeddable ZIP does not match pinned 3.12.10 release")
    if digest(args.xdelta) != XDELTA_SHA256:
        raise ValueError("xdelta3.exe does not match pinned v3.2.0 release")
    source = Path(__file__).resolve().parent
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(args.output, "w") as archive:
        with zipfile.ZipFile(args.python_embed) as runtime:
            for name in runtime.namelist():
                if name.endswith("/"):
                    continue
                data = runtime.read(name)
                if name == "python312._pth":
                    # Scripts live one directory above python.exe. The vendored
                    # TTARCH2 package is loaded from tools/vendor by the helper.
                    data = b"python312.zip\n.\n..\n"
                add(archive, "python/" + name, data)
        add(archive, "python/xdelta3.exe", args.xdelta.read_bytes())
        add(archive, "licenses/xdelta3.LICENSE", args.xdelta_license.read_bytes())
        for name in ("apply_patches.py", "patch_pipeline.py"):
            add(archive, name, (source / name).read_bytes())
        vendor = source / "vendor"
        for path in sorted(vendor.rglob("*")):
            if path.is_file() and "__pycache__" not in path.parts and path.suffix != ".pyc":
                add(archive, "vendor/" + path.relative_to(vendor).as_posix(), path.read_bytes())
    print(f"Packed {args.output} ({args.output.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
