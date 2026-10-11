from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path

from .blowfish import BlowfishV7
from .profiles import GameProfile, resolve_profile
from .ttarch2 import Ttarch2Archive, pack_archive


LENC_MAGIC = b"\x1bLEo"
SAFE_NAME = re.compile(r"[A-Za-z0-9_.-]+\Z")
ARCHIVE_PREFIXES = {
    "mcsm-season-one": "MCSM_pc_",
    "mcsm-season-two": "MC2_pc_",
}


def _checked_name(label: str, value: str) -> str:
    if not SAFE_NAME.fullmatch(value):
        raise ValueError(
            f"{label} must contain only ASCII letters, digits, dot, underscore, or hyphen"
        )
    return value


def _checked_priority(value: int) -> int:
    if not 0 <= value <= 2_147_483_647:
        raise ValueError("priority must be between 0 and 2147483647")
    return value


@dataclass(frozen=True)
class SidecarDescriptor:
    name: str
    set_name: str
    logical_name: str
    destination: str
    archive_name: str
    descriptor_name: str
    priority: int
    enable_mode: str

    def validated(self) -> SidecarDescriptor:
        for label, value in (
            ("name", self.name),
            ("set name", self.set_name),
            ("logical name", self.logical_name),
            ("destination", self.destination),
            ("archive name", self.archive_name),
            ("descriptor name", self.descriptor_name),
        ):
            _checked_name(label, value)
        _checked_priority(self.priority)
        if self.enable_mode not in {"constant", "localization"}:
            raise ValueError("enable mode must be constant or localization")
        return self

    def render(self) -> str:
        self.validated()
        fields = (
            "local set = {}",
            f'set.name = "{self.name}"',
            f'set.setName = "{self.set_name}"',
            f'set.descriptionFilenameOverride = "{self.descriptor_name}"',
            f'set.logicalName = "<{self.logical_name}>"',
            f'set.logicalDestination = "<{self.destination}>"',
            f"set.priority = {self.priority}",
            "set.localDir = _currentDirectory",
            f'set.enableMode = "{self.enable_mode}"',
            'set.version = "patch"',
            "set.descriptionPriority = 900",
            f'set.gameDataName = "{self.name} Game Data"',
            "set.gameDataPriority = 0",
            'set.gameDataEnableMode = "constant"',
            "set.localDirIncludeBase = true",
            "set.localDirRecurse = false",
            "set.localDirIncludeOnly = nil",
            "set.localDirExclude =",
            "{",
            '    "_dev/"',
            "}",
            "set.gameDataArchives =",
            "{",
            f'    _currentDirectory .. "{self.archive_name}"',
            "}",
            "RegisterSetDescription(set)",
        )
        return "\n".join(fields) + "\n"


def encrypt_lenc(plain: bytes, profile: str | GameProfile) -> bytes:
    selected = resolve_profile(profile)
    return LENC_MAGIC + BlowfishV7(selected.key).encrypt_ecb(plain)


def decrypt_lenc(data: bytes, profile: str | GameProfile) -> bytes:
    if not data.startswith(LENC_MAGIC):
        raise ValueError("resource descriptor does not have a LENC header")
    selected = resolve_profile(profile)
    return BlowfishV7(selected.key).decrypt_ecb(data[len(LENC_MAGIC) :])


def _write_verified(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_bytes(data)
    temporary.replace(path)
    if path.read_bytes() != data:
        raise AssertionError(f"write verification failed: {path}")


def build_sidecar_patch(
    input_dir: str | Path,
    output_dir: str | Path,
    *,
    name: str,
    destination: str,
    priority: int = 900,
    enable_mode: str = "constant",
    set_name: str | None = None,
    logical_name: str | None = None,
    profile: str | GameProfile = "mcsm-season-one",
) -> dict[str, object]:
    source = Path(input_dir).resolve()
    output = Path(output_dir).resolve()
    if not source.is_dir():
        raise ValueError(f"patch input directory does not exist: {source}")
    if output == source or source in output.parents:
        raise ValueError("patch output directory must be separate from the input tree")

    selected = resolve_profile(profile)
    safe_name = _checked_name("name", name)
    safe_destination = _checked_name("destination", destination)
    prefix = ARCHIVE_PREFIXES[selected.name]
    archive_name = f"{prefix}{safe_name}.ttarch2"
    descriptor_name = f"_resdesc_50_{safe_name}.lua"
    descriptor = SidecarDescriptor(
        name=safe_name,
        set_name=_checked_name("set name", set_name or safe_name),
        logical_name=_checked_name("logical name", logical_name or safe_name),
        destination=safe_destination,
        archive_name=archive_name,
        descriptor_name=descriptor_name,
        priority=_checked_priority(priority),
        enable_mode=enable_mode,
    ).validated()
    plain_descriptor = descriptor.render().encode("utf-8")
    encrypted_descriptor = encrypt_lenc(plain_descriptor, selected)

    output.mkdir(parents=True, exist_ok=True)
    archive_path = output / archive_name
    descriptor_path = output / descriptor_name
    packed_count = pack_archive(str(source), str(archive_path))
    _write_verified(descriptor_path, encrypted_descriptor)

    archive = Ttarch2Archive(archive_path)
    source_files = sorted(path for path in source.rglob("*") if path.is_file())
    expected = {path.name: path.read_bytes() for path in source_files}
    actual = {entry.name: archive.read_file(entry) for entry in archive.files}
    if actual != expected:
        raise AssertionError("packed sidecar archive does not match the input files")
    if decrypt_lenc(descriptor_path.read_bytes(), selected) != plain_descriptor:
        raise AssertionError("resource descriptor encryption round-trip failed")

    return {
        "archive": str(archive_path),
        "descriptor": str(descriptor_path),
        "files": packed_count,
        "profile": selected.name,
        "name": safe_name,
        "set_name": descriptor.set_name,
        "logical_destination": safe_destination,
        "priority": descriptor.priority,
        "enable_mode": descriptor.enable_mode,
    }
