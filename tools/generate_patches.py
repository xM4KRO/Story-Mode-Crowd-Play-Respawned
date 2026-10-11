"""Generate xdelta3 patches from the locally owned, exact GOG game builds.

This private developer tool reads the existing modified CrowdPlay sidecars but
does not edit the games, the old installer, or any existing mod source.
"""

from __future__ import annotations

import argparse
from pathlib import Path

from patch_pipeline import generate_season


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--season1-game-root", type=Path, required=True)
    parser.add_argument("--season2-game-root", type=Path, required=True)
    parser.add_argument("--build-root", type=Path, required=True,
                        help="Existing private build folder containing s1/Archives and s2/Archives")
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--xdelta", type=Path, required=True,
                        help="Path to an xdelta3 executable")
    args = parser.parse_args()
    for season, root in (("s1", args.season1_game_root),
                         ("s2", args.season2_game_root)):
        manifest = generate_season(season, root, args.build_root,
                                   args.output_dir, args.xdelta)
        entries = sum(len(group["entries"]) for group in manifest["archives"])
        print(f"{season}: {entries} entries prepared at {args.output_dir / season / 'patches'}")


if __name__ == "__main__":
    main()
