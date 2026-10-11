from __future__ import annotations

import struct

from ._blowfish_constants import P_INIT, S_INIT
from .profiles import MCSM_SEASON_ONE, MCSM_SEASON_TWO


MASK_32 = 0xFFFFFFFF
MCSM_SEASON_ONE_KEY = MCSM_SEASON_ONE.key
MCSM_SEASON_TWO_KEY = MCSM_SEASON_TWO.key


def _reverse_bytes_32(value: int) -> int:
    return int.from_bytes(value.to_bytes(4, "big"), "little")


class BlowfishV7:
    """Blowfish variant used by supported MCSM PC archives."""

    def __init__(self, key: bytes = MCSM_SEASON_ONE_KEY) -> None:
        if not 1 <= len(key) <= 56:
            raise ValueError("Blowfish keys must contain between 1 and 56 bytes")
        self._p = list(P_INIT)
        self._s = [list(box) for box in S_INIT]
        self._s[0][118] = _reverse_bytes_32(self._s[0][118])
        self._expand_key(key)

    def _round_function(self, word: int) -> int:
        a, b, c, d = word.to_bytes(4, "big")
        value = (self._s[0][a] + self._s[1][b]) & MASK_32
        value ^= self._s[2][c]
        return (value + self._s[3][d]) & MASK_32

    def _standard_encrypt(self, left: int, right: int) -> tuple[int, int]:
        for round_index in range(16):
            left ^= self._p[round_index]
            right ^= self._round_function(left)
            left, right = right & MASK_32, left & MASK_32
        left, right = right, left
        right ^= self._p[16]
        left ^= self._p[17]
        return left & MASK_32, right & MASK_32

    @staticmethod
    def _encrypt_schedule(round_index: int) -> int:
        return {1: 3, 2: 4, 3: 1, 4: 2}.get(round_index, round_index)

    def _variant_encrypt(self, left: int, right: int) -> tuple[int, int]:
        for round_index in range(16):
            left ^= self._p[self._encrypt_schedule(round_index)]
            right ^= self._round_function(left)
            left, right = right & MASK_32, left & MASK_32
        left, right = right, left
        return (left ^ self._p[17]) & MASK_32, (right ^ self._p[16]) & MASK_32

    def _variant_decrypt(self, left: int, right: int) -> tuple[int, int]:
        schedule = [17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 2, 1, 4]
        for p_index in schedule:
            left ^= self._p[p_index]
            right ^= self._round_function(left)
            left, right = right & MASK_32, left & MASK_32
        left, right = right, left
        return (left ^ self._p[0]) & MASK_32, (right ^ self._p[3]) & MASK_32

    def _expand_key(self, key: bytes) -> None:
        cursor = 0
        for index in range(len(self._p)):
            word = 0
            for _ in range(4):
                word = (word << 8) | key[cursor]
                cursor = (cursor + 1) % len(key)
            self._p[index] ^= word

        left = right = 0
        for index in range(0, len(self._p), 2):
            left, right = self._standard_encrypt(left, right)
            self._p[index : index + 2] = (left, right)
        for box in self._s:
            for index in range(0, len(box), 2):
                left, right = self._standard_encrypt(left, right)
                box[index : index + 2] = (left, right)

    def _crypt(self, data: bytes, *, decrypt: bool) -> bytes:
        result = bytearray(data)
        complete = len(result) - (len(result) % 8)
        operation = self._variant_decrypt if decrypt else self._variant_encrypt
        for offset in range(0, complete, 8):
            left, right = struct.unpack_from("<II", result, offset)
            left, right = operation(left, right)
            struct.pack_into("<II", result, offset, left, right)
        return bytes(result)

    def decrypt_ecb(self, data: bytes) -> bytes:
        return self._crypt(data, decrypt=True)

    def encrypt_ecb(self, data: bytes) -> bytes:
        return self._crypt(data, decrypt=False)
