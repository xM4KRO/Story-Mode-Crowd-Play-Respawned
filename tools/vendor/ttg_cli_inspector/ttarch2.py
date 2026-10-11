from __future__ import annotations

import fnmatch
import os
import struct
import zlib
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Mapping

from .blowfish import BlowfishV7
from .crc import crc64_name
from .profiles import GameProfile, resolve_profile


MAX_CHUNK_SIZE = 16 * 1024 * 1024
MAX_ENTRY_COUNT = 1_000_000
ENTRY_STRUCT = struct.Struct("<QQIIHH")


@dataclass(frozen=True)
class Ttarch2File:
    name: str
    offset: int
    size: int
    crc64: int
    unknown: int = 0


class _Cursor:
    def __init__(self, data: bytes, offset: int = 0) -> None:
        self.data = data
        self.offset = offset

    def take(self, size: int) -> bytes:
        if size < 0 or self.offset + size > len(self.data):
            raise ValueError("truncated TTARCH2 structure")
        value = self.data[self.offset : self.offset + size]
        self.offset += size
        return value

    def u32(self) -> int:
        return struct.unpack("<I", self.take(4))[0]

    def u64(self) -> int:
        return struct.unpack("<Q", self.take(8))[0]


class Ttarch2Archive:
    def __init__(
        self,
        path: str | os.PathLike[str],
        profile: str | GameProfile | None = "auto",
    ) -> None:
        self.path = Path(path)
        self.data = self.path.read_bytes()
        if len(self.data) < 12:
            raise ValueError("TTARCH2 file is shorter than its header")
        self.magic = self.data[:4]
        self.profile = resolve_profile(profile, self.path.name)
        self.is_compressed = self.magic != b"NCTT"
        self.is_encrypted = self.magic in {b"ECTT", b"eCTT"}
        self.compress_algorithm = 0
        self.chunk_size = 0
        self.chunk_sizes: list[int] = []
        self.compressed_data_offset = 0
        self.files_offset = 0
        self.version = 0
        self.files: list[Ttarch2File] = []
        self._chunk_cache: dict[int, bytes] = {}
        self._cipher = BlowfishV7(self.profile.key)
        self._parse()

    def _inflate(self, payload: bytes) -> bytes:
        if self.compress_algorithm == 2:
            raise NotImplementedError(
                "Oodle-compressed eCTT/zCTT archives are not supported"
            )
        errors: list[zlib.error] = []
        for window_bits in (-15, zlib.MAX_WBITS):
            try:
                inflater = zlib.decompressobj(window_bits)
                output = inflater.decompress(payload, self.chunk_size + 1)
                if len(output) > self.chunk_size or not inflater.eof:
                    raise ValueError("compressed TTARCH2 chunk exceeds its declared size")
                return output
            except zlib.error as error:
                errors.append(error)
        raise ValueError(f"invalid deflate chunk: {errors[-1]}")

    def _read_chunk(self, index: int) -> bytes:
        if not 0 <= index < len(self.chunk_sizes):
            raise ValueError(f"TTARCH2 chunk index is out of range: {index}")
        cached = self._chunk_cache.get(index)
        if cached is not None:
            return cached
        relative = sum(self.chunk_sizes[:index])
        size = self.chunk_sizes[index]
        start = self.compressed_data_offset + relative
        end = start + size
        if end > len(self.data):
            raise ValueError("TTARCH2 chunk extends beyond the file")
        payload = self.data[start:end]
        if self.is_encrypted:
            payload = self._cipher.decrypt_ecb(payload)
        decoded = payload if len(payload) == self.chunk_size else self._inflate(payload)
        if len(decoded) > self.chunk_size:
            raise ValueError("decoded TTARCH2 chunk is too large")
        self._chunk_cache[index] = decoded
        return decoded

    def _logical_prefix(self, required: int) -> bytes:
        if required < 0:
            raise ValueError("negative logical TTARCH2 size")
        parts: list[bytes] = []
        total = 0
        for index in range(len(self.chunk_sizes)):
            chunk = self._read_chunk(index)
            parts.append(chunk)
            total += len(chunk)
            if total >= required:
                return b"".join(parts)
        result = b"".join(parts)
        if len(result) < required:
            raise ValueError("TTARCH2 logical stream is truncated")
        return result

    def _parse_compressed_header(self) -> bytes:
        cursor = _Cursor(self.data, 4)
        if self.magic in {b"eCTT", b"zCTT"}:
            marker = cursor.u32()
            if marker != 1:
                raise ValueError(f"unsupported TTARCH2 compression marker {marker}")
            self.compress_algorithm = 2
        else:
            self.compress_algorithm = 1
        self.chunk_size = cursor.u32()
        chunk_count = cursor.u32()
        if not 1 <= self.chunk_size <= MAX_CHUNK_SIZE:
            raise ValueError(f"invalid TTARCH2 chunk size {self.chunk_size}")
        if chunk_count > MAX_ENTRY_COUNT:
            raise ValueError(f"unreasonable TTARCH2 chunk count {chunk_count}")
        previous = cursor.u64()
        cumulative: list[int] = []
        for _ in range(chunk_count):
            current = cursor.u64()
            if current < previous:
                raise ValueError("TTARCH2 chunk offsets are not monotonic")
            cumulative.append(current - previous)
            previous = current
        self.chunk_sizes = cumulative
        self.compressed_data_offset = cursor.offset
        if self.compressed_data_offset + sum(cumulative) > len(self.data):
            raise ValueError("TTARCH2 compressed stream extends beyond the file")
        return self._logical_prefix(32)

    def _parse(self) -> None:
        if self.magic == b"NCTT":
            logical = self.data[12:]
        elif self.magic in {b"ZCTT", b"ECTT", b"zCTT", b"eCTT"}:
            logical = self._parse_compressed_header()
        else:
            raise ValueError(f"unsupported TTARCH2 magic {self.magic!r}")

        if len(logical) < 12:
            raise ValueError("TTARCH2 logical header is truncated")
        subheader = logical[:4]
        if subheader == b"3ATT":
            self.version = 1
            table_offset = 8
        elif subheader == b"4ATT":
            self.version = 2
            table_offset = 4
        else:
            raise ValueError(f"unsupported TTARCH2 table header {subheader!r}")

        header = _Cursor(logical, table_offset)
        names_size = header.u32()
        entry_count = header.u32()
        if entry_count > MAX_ENTRY_COUNT:
            raise ValueError(f"unreasonable TTARCH2 entry count {entry_count}")
        required = header.offset + entry_count * ENTRY_STRUCT.size + names_size
        if self.is_compressed and len(logical) < required:
            logical = self._logical_prefix(required)
        if required > len(logical):
            raise ValueError("TTARCH2 entry or name table is truncated")

        table_start = header.offset
        names_start = table_start + entry_count * ENTRY_STRUCT.size
        self.files_offset = names_start + names_size
        parsed: list[Ttarch2File] = []
        for index in range(entry_count):
            record_offset = table_start + index * ENTRY_STRUCT.size
            checksum, file_offset, size, flags, name_block, name_offset = ENTRY_STRUCT.unpack_from(
                logical, record_offset
            )
            relative_name = name_block * 0x10000 + name_offset
            if relative_name >= names_size:
                raise ValueError(f"TTARCH2 entry {index} points outside the name table")
            name_start = names_start + relative_name
            name_end = logical.find(b"\0", name_start, names_start + names_size)
            if name_end < 0:
                raise ValueError(f"TTARCH2 entry {index} has an unterminated name")
            try:
                name = logical[name_start:name_end].decode("ascii")
            except UnicodeDecodeError as error:
                raise ValueError(f"TTARCH2 entry {index} has a non-ASCII name") from error
            parsed.append(Ttarch2File(name, file_offset, size, checksum, flags))
        self.files = parsed

        # Compressed archives are validated lazily per chunk. Using the declared
        # chunk capacity here avoids decompressing multi-gigabyte archives merely
        # to list their entries while still rejecting impossible table offsets.
        logical_size = (
            self.chunk_size * len(self.chunk_sizes)
            if self.is_compressed
            else len(logical)
        )
        for entry in self.files:
            if entry.offset + entry.size > logical_size - self.files_offset:
                raise ValueError(f"TTARCH2 entry extends beyond the data stream: {entry.name}")

    def read_file(self, entry: Ttarch2File) -> bytes:
        absolute = self.files_offset + entry.offset
        if not self.is_compressed:
            start = 12 + absolute
            end = start + entry.size
            if end > len(self.data):
                raise ValueError(f"TTARCH2 entry is truncated: {entry.name}")
            return self.data[start:end]
        if entry.size == 0:
            return b""
        first = absolute // self.chunk_size
        last = (absolute + entry.size - 1) // self.chunk_size
        block = b"".join(self._read_chunk(index) for index in range(first, last + 1))
        inner = absolute - first * self.chunk_size
        return block[inner : inner + entry.size]


def list_archive(
    archive_path: str,
    pattern: str | None = None,
    *,
    profile: str | GameProfile | None = "auto",
) -> list[Ttarch2File]:
    files = Ttarch2Archive(archive_path, profile=profile).files
    return [entry for entry in files if fnmatch.fnmatch(entry.name, pattern)] if pattern else files


def _safe_output(root: Path, archive_name: str) -> Path:
    normalized = archive_name.replace("\\", "/")
    pure = PurePosixPath(normalized)
    if pure.is_absolute() or not pure.parts or any(part in {"", ".", ".."} for part in pure.parts):
        raise ValueError(f"unsafe archive entry path: {archive_name!r}")
    if ":" in pure.parts[0]:
        raise ValueError(f"unsafe archive entry path: {archive_name!r}")
    destination = root.joinpath(*pure.parts).resolve()
    resolved_root = root.resolve()
    try:
        destination.relative_to(resolved_root)
    except ValueError as error:
        raise ValueError(f"archive entry escapes the output directory: {archive_name!r}") from error
    return destination


def unpack_archive(
    archive_path: str,
    out_dir: str,
    pattern: str | None = None,
    *,
    profile: str | GameProfile | None = "auto",
) -> int:
    archive = Ttarch2Archive(archive_path, profile=profile)
    root = Path(out_dir)
    root.mkdir(parents=True, exist_ok=True)
    extracted = 0
    for entry in archive.files:
        if pattern and not fnmatch.fnmatch(entry.name, pattern):
            continue
        destination = _safe_output(root, entry.name)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(archive.read_file(entry))
        extracted += 1
    return extracted


def _input_files(folder: str | os.PathLike[str]) -> tuple[Path, list[Path]]:
    root = Path(folder)
    if not root.is_dir():
        raise FileNotFoundError(f"input directory not found: {root}")
    files = sorted(path for path in root.rglob("*") if path.is_file())
    if not files:
        raise ValueError(f"no files to pack in {root}")
    return root, files


def pack_archive(folder: str, output_path: str) -> int:
    root, files = _input_files(folder)
    groups: dict[str, list[Path]] = {}
    for path in files:
        groups.setdefault(path.name, []).append(path)
    duplicates = {name: paths for name, paths in groups.items() if len(paths) > 1}
    if duplicates:
        details = "; ".join(
            f"{name}: " + ", ".join(str(path.relative_to(root)) for path in paths[:4])
            for name, paths in sorted(duplicates.items())[:8]
        )
        raise ValueError(f"duplicate basenames cannot be packed into one TTARCH2: {details}")
    records = [
        (name, paths[0].read_bytes(), crc64_name(name), 0)
        for name, paths in groups.items()
    ]
    records.sort(key=lambda item: (item[2], item[0]))
    _write_nctt(records, Path(output_path))
    return len(records)


def _write_nctt(records: list[tuple[str, bytes, int, int]], output: Path) -> None:
    encoded_names = [name.encode("ascii") + b"\0" for name, *_ in records]
    raw_names_size = sum(map(len, encoded_names))
    names_size = ((raw_names_size + 0xFFFF) // 0x10000) * 0x10000
    names = bytearray(names_size)
    table = bytearray()
    name_cursor = 0
    data_cursor = 0
    for (name, payload, checksum, flags), encoded in zip(records, encoded_names):
        del name
        names[name_cursor : name_cursor + len(encoded)] = encoded
        table.extend(
            ENTRY_STRUCT.pack(
                checksum,
                data_cursor,
                len(payload),
                flags,
                name_cursor // 0x10000,
                name_cursor % 0x10000,
            )
        )
        name_cursor += len(encoded)
        data_cursor += len(payload)
    logical = b"4ATT" + struct.pack("<II", names_size, len(records)) + table + names
    logical += b"".join(payload for _, payload, _, _ in records)
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name(output.name + ".tmp")
    temporary.write_bytes(b"NCTT" + struct.pack("<Q", 12 + len(logical)) + logical)
    temporary.replace(output)


def repack_archive(
    source_path: str | os.PathLike[str],
    replacements: Mapping[str, bytes | str | os.PathLike[str]],
    output_path: str | os.PathLike[str],
    *,
    profile: str | GameProfile | None = "auto",
) -> int:
    source = Path(source_path)
    output = Path(output_path)
    if source.resolve() == output.resolve():
        raise ValueError("source and output archive paths must differ")
    archive = Ttarch2Archive(source, profile=profile)
    requested = {name.casefold(): value for name, value in replacements.items()}
    existing = {entry.name.casefold() for entry in archive.files}
    missing = sorted(requested.keys() - existing)
    if missing:
        raise ValueError("replacement entries are absent from the source archive: " + ", ".join(missing[:8]))
    records: list[tuple[str, bytes, int, int]] = []
    replaced = 0
    for entry in archive.files:
        candidate = requested.get(entry.name.casefold())
        if candidate is None:
            payload = archive.read_file(entry)
        elif isinstance(candidate, bytes):
            payload = candidate
            replaced += 1
        else:
            payload = Path(candidate).read_bytes()
            replaced += 1
        records.append((entry.name, payload, entry.crc64, entry.unknown))
    _write_nctt(records, output)
    return replaced
