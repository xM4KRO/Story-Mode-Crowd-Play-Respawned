from __future__ import annotations


CRC64_ECMA_POLYNOMIAL = 0x42F0E1EBA9EA3693
MASK_64 = 0xFFFFFFFFFFFFFFFF


def crc64_name(name: str) -> int:
    """Return the case-insensitive ECMA-182 CRC used for archive entry names."""

    try:
        encoded = name.casefold().encode("ascii")
    except UnicodeEncodeError as error:
        raise ValueError("archive entry names must be ASCII") from error
    checksum = 0
    for byte in encoded:
        checksum ^= byte << 56
        for _ in range(8):
            high_bit = checksum & (1 << 63)
            checksum = (checksum << 1) & MASK_64
            if high_bit:
                checksum ^= CRC64_ECMA_POLYNOMIAL
    return checksum

