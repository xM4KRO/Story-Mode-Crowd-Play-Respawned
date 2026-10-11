"""Reconstruct the original CrowdPlay sidecars from a user's own game files.

This installer helper stages output only under --output-dir. It never changes
the original game archives; the installer commits the verified output later.
"""

from __future__ import annotations

import argparse
from pathlib import Path

from patch_pipeline import apply_season


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-root", type=Path, required=True)
    parser.add_argument("--season", choices=("s1", "s2"), required=True)
    parser.add_argument("--payload-dir", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--xdelta", type=Path, required=True)
    args = parser.parse_args()
    files = apply_season(args.season, args.game_root, args.payload_dir,
                         args.output_dir, args.xdelta)
    for path in files:
        print(path.relative_to(args.output_dir).as_posix())


if __name__ == "__main__":
    main()
