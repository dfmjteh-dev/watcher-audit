#!/usr/bin/env python3
"""Версия файла (VS_FIXEDFILEINFO.dwFileVersion) из ресурсов PE — то, что
Windows Installer сравнивает при обновлении. wixl колонку File.Version не
заполняет, а без неё MSI считает новый exe «младше» установленного и не ставит
его (build-msi.sh). Ищется только в секции .rsrc самого PE: в single-file .NET
внутри лежат ещё и ресурсы рантайма со своей версией — берётся ресурс
RT_VERSION, как у Windows.

    pe-version.py Watcher.Capture.exe   → 0.10.0.0 (без ресурса версии — пусто)
"""
import struct
import sys

SIGNATURE = b"\xbd\x04\xef\xfe"


RT_VERSION = 16


def _first_entry(data, base, at, want_id=None):
    """Запись каталога ресурсов: с want_id — по типу, иначе первая."""
    named, ids = struct.unpack_from("<HH", data, at + 12)
    for i in range(named + ids):
        name, off = struct.unpack_from("<II", data, at + 16 + 8 * i)
        if want_id is None or (not name & 0x80000000 and name == want_id):
            return off
    return None


def file_version(path):
    with open(path, "rb") as f:
        data = f.read()
    if data[:2] != b"MZ":
        return ""
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        return ""
    sections = struct.unpack_from("<H", data, pe + 6)[0]
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    table = pe + 24 + opt_size
    for i in range(sections):
        s = table + 40 * i
        if data[s:s + 8].rstrip(b"\0") != b".rsrc":
            continue
        va, raw = struct.unpack_from("<I", data, s + 12)[0], struct.unpack_from("<I", data, s + 20)[0]
        # Дерево ресурсов: тип RT_VERSION → первое имя → первый язык → данные.
        # Так выбирает и Windows; поиск сигнатуры по всей .rsrc находит версию
        # рантайма, которую single-file .NET тоже несёт в ресурсах.
        off = _first_entry(data, raw, raw, RT_VERSION)
        for _ in range(2):
            if off is None or not off & 0x80000000:
                return ""
            off = _first_entry(data, raw, raw + (off & 0x7FFFFFFF))
        if off is None or off & 0x80000000:
            return ""
        rva, size = struct.unpack_from("<II", data, raw + off)
        start = raw + rva - va
        at = data.find(SIGNATURE, start, start + size)
        if at < 0:
            return ""
        ms, ls = struct.unpack_from("<II", data, at + 8)
        return f"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}"
    return ""


if __name__ == "__main__":
    print(file_version(sys.argv[1]))
