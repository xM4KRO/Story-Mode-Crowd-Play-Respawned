from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class GameProfile:
    name: str
    archive_prefixes: tuple[str, ...]
    key: bytes


MCSM_SEASON_ONE = GameProfile(
    name="mcsm-season-one",
    archive_prefixes=("mcsm_",),
    key=bytes.fromhex(
        "8cd29b9987de94a9e69da5947fcec1bccc9c8eddb1a6669fb28addba9cdec29ad376627faecca7d1d8e6d9d2ab63829f92cc9498d3e4a0"
    ),
)

MCSM_SEASON_TWO = GameProfile(
    name="mcsm-season-two",
    archive_prefixes=("mc2_",),
    key=bytes.fromhex(
        "8cd29b9987de94a9e69ea5947fcec1bccc9c8eddb1a7669fb28addba9cdec29ad377627faecca7d1d8e6d9d2ab64829f92cc9498d3e4a0"
    ),
)

PROFILES = {
    MCSM_SEASON_ONE.name: MCSM_SEASON_ONE,
    MCSM_SEASON_TWO.name: MCSM_SEASON_TWO,
}


def resolve_profile(selection: str | GameProfile | None, archive_name: str = "") -> GameProfile:
    if isinstance(selection, GameProfile):
        return selection
    if selection and selection != "auto":
        try:
            return PROFILES[selection]
        except KeyError as error:
            choices = ", ".join(sorted(PROFILES))
            raise ValueError(f"unknown game profile {selection!r}; choose {choices} or auto") from error
    lowered = archive_name.casefold()
    for profile in PROFILES.values():
        if lowered.startswith(profile.archive_prefixes):
            return profile
    return MCSM_SEASON_ONE

