#!/usr/bin/env python3
"""Порядок строк в таблицах MSI — по первичному ключу, как требует Windows.

Windows Installer хранит строки каждой таблицы отсортированными по значениям
ключевых колонок (у строковых — по номеру строки в _StringPool) и таблицу с
нарушенным порядком не загружает: 2211/2229 «could not load table», а у
Property — «Failing install, missing product code», msiexec отвечает 1605
ещё до начала установки. msibuild (msitools) вставляет новую строку в конец
таблицы и при этом переиспользует освободившиеся номера строк — после
UPDATE/INSERT в build-msi.sh ключ новой строки бывает меньше предыдущих
(так сломался MSI 0.15.2: WATCHERMIGRATION получил номер 98 после 142).
msiinfo/msibuild такой MSI читают без ошибок — проверить можно только этим
скриптом или на Windows.

Скрипт переставляет строки на месте: длина потоков таблиц не меняется, поэтому
структура составного файла (FAT, каталог) остаётся прежней. Запускать после
всех правок таблиц и до подписи.

    msi-table-order.py file.msi           — упорядочить (печатает исправленные таблицы)
    msi-table-order.py --check file.msi   — только проверить, код 1 при нарушении
"""
import struct
import sys

FREE, END = 0xFFFFFFFF, 0xFFFFFFFE
CHARS = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._"

# Биты типа колонки (_Columns.Type), как в msi.h/Wine.
T_VALID, T_STRING, T_NULLABLE, T_KEY = 0x0100, 0x0800, 0x1000, 0x2000


def stream_name(raw):
    """Имя потока MSI: таблицы — «!Имя», символы упакованы по два в U+3800..U+47FF."""
    out = []
    for ch in raw:
        c = ord(ch)
        if 0x3800 <= c < 0x4800:
            c -= 0x3800
            out.append(CHARS[c & 0x3F] + CHARS[(c >> 6) & 0x3F])
        elif 0x4800 <= c < 0x4840:
            out.append(CHARS[c - 0x4800])
        elif c == 0x4840:
            out.append("!")
        else:
            out.append(ch)
    return "".join(out)


class Cfb:
    """Минимальный составной файл (MS-CFB): чтение потоков и запись той же длины."""

    def __init__(self, f):
        self.f = f
        h = self.read_at(0, 512)
        if h[:8] != bytes.fromhex("d0cf11e0a1b11ae1"):
            raise ValueError("не составной файл (не MSI)")
        self.ssz = 1 << struct.unpack_from("<H", h, 0x1E)[0]
        self.mssz = 1 << struct.unpack_from("<H", h, 0x20)[0]
        dir_start, = struct.unpack_from("<I", h, 0x30)
        self.cutoff, minifat_start = struct.unpack_from("<II", h, 0x38)
        difat_next, difat_count = struct.unpack_from("<II", h, 0x44)
        fat_sectors = [s for s in struct.unpack_from("<109I", h, 0x4C) if s not in (FREE, END)]
        per = self.ssz // 4
        for _ in range(difat_count):
            if difat_next in (FREE, END):
                break
            d = struct.unpack("<%dI" % per, self.sector(difat_next))
            fat_sectors += [s for s in d[:-1] if s not in (FREE, END)]
            difat_next = d[-1]
        self.fat = []
        for s in fat_sectors:
            self.fat += struct.unpack("<%dI" % per, self.sector(s))
        self.entries = {}
        dirdata = b"".join(self.sector(s) for s in self.chain(dir_start))
        for i in range(len(dirdata) // 128):
            e = dirdata[i * 128:(i + 1) * 128]
            nlen, = struct.unpack_from("<H", e, 64)
            kind = e[66]
            if kind == 0 or nlen < 2:
                continue
            start, size = struct.unpack_from("<IQ", e, 116)
            if self.ssz == 512:
                size &= 0xFFFFFFFF
            name = e[:nlen - 2].decode("utf-16-le")
            if kind == 5:
                self.ministream = self.chain(start)
            elif kind == 2:
                self.entries[stream_name(name)] = (start, size)
        self.minifat = []
        for s in self.chain(minifat_start):
            self.minifat += struct.unpack("<%dI" % per, self.sector(s))

    def read_at(self, off, n):
        self.f.seek(off)
        data = self.f.read(n)
        if len(data) != n:
            raise ValueError("файл обрезан")
        return data

    def sector(self, s):
        return self.read_at((s + 1) * self.ssz, self.ssz)

    def chain(self, start, fat=None):
        fat = self.fat if fat is None else fat
        out, seen = [], set()
        while start not in (END, FREE):
            if start in seen or start >= len(fat):
                raise ValueError("испорченная цепочка секторов")
            seen.add(start)
            out.append(start)
            start = fat[start]
        return out

    def segments(self, name):
        """(смещение в файле, длина) кусков потока по порядку."""
        start, size = self.entries[name]
        segs = []
        if size < self.cutoff:
            per = self.ssz // self.mssz
            for m in self.chain(start, self.minifat):
                big = self.ministream[m // per]
                segs.append(((big + 1) * self.ssz + (m % per) * self.mssz, self.mssz))
        else:
            segs = [((s + 1) * self.ssz, self.ssz) for s in self.chain(start)]
        out, left = [], size
        for off, n in segs:
            if left <= 0:
                break
            out.append((off, min(n, left)))
            left -= n
        if left > 0:
            raise ValueError("поток %s короче заявленного" % name)
        return out

    def read(self, name):
        return b"".join(self.read_at(o, n) for o, n in self.segments(name))

    def write(self, name, data):
        segs = self.segments(name)
        if len(data) != sum(n for _, n in segs):
            raise ValueError("длина потока %s изменилась" % name)
        pos = 0
        for off, n in segs:
            self.f.seek(off)
            self.f.write(data[pos:pos + n])
            pos += n


def strings(cfb):
    """Номер → строка из _StringPool/_StringData; размер ссылки на строку (2 или 3 байта)."""
    pool, data = cfb.read("!_StringPool"), cfb.read("!_StringData")
    header, = struct.unpack_from("<I", pool, 0)
    out, off = {}, 0
    for i in range(1, len(pool) // 4):
        n, refs = struct.unpack_from("<HH", pool, i * 4)
        if n == 0 and refs != 0:
            raise ValueError("строки длиннее 64 КБ не поддерживаются")
        out[i] = data[off:off + n].decode("cp1252", "replace")
        off += n
    return out, 3 if header & 0x80000000 else 2


def column_bytes(kind, ref):
    if kind & ~T_NULLABLE == T_STRING | T_VALID:  # OBJECT (поток)
        return 2
    if kind & T_STRING:
        return ref
    if kind & 0xFF <= 2:
        return 2
    if kind & 0xFF == 4:
        return 4
    raise ValueError("неизвестный тип колонки %#x" % kind)


def split(data, sizes):
    row = sum(sizes)
    if not row or len(data) % row:
        raise ValueError("размер потока не кратен размеру строки")
    rows = len(data) // row
    cols, pos = [], 0
    for size in sizes:
        cols.append([data[pos + r * size:pos + (r + 1) * size] for r in range(rows)])
        pos += rows * size
    return cols, rows


def process(path, check):
    with open(path, "rb" if check else "r+b") as f:
        cfb = Cfb(f)
        names, ref = strings(cfb)
        cols, rows = split(cfb.read("!_Columns"), [ref, 2, ref, 2])
        schema = {}
        for r in range(rows):
            table = names[int.from_bytes(cols[0][r], "little")]
            number = int.from_bytes(cols[1][r], "little") ^ 0x8000
            kind = int.from_bytes(cols[3][r], "little") ^ 0x8000
            schema.setdefault(table, []).append((number, kind))
        bad = []
        for table, cdefs in sorted(schema.items()):
            if table.startswith("_") or "!" + table not in cfb.entries:
                continue
            cdefs.sort()
            sizes = [column_bytes(k, ref) for _, k in cdefs]
            keys = [i for i, (_, k) in enumerate(cdefs) if k & T_KEY]
            data = cfb.read("!" + table)
            if not data:
                continue
            tcols, trows = split(data, sizes)
            key = lambda r: tuple(int.from_bytes(tcols[i][r], "little") for i in keys)
            order = sorted(range(trows), key=key)
            if any(key(a) == key(b) for a, b in zip(order, order[1:])):
                raise ValueError("повтор первичного ключа в таблице %s" % table)
            if order == list(range(trows)):
                continue
            bad.append(table)
            if not check:
                cfb.write("!" + table, b"".join(b"".join(c[r] for r in order) for c in tcols))
        return bad


def main():
    args = sys.argv[1:]
    check = args[:1] == ["--check"]
    if check:
        args = args[1:]
    if len(args) != 1:
        print("msi-table-order.py [--check] file.msi", file=sys.stderr)
        sys.exit(2)
    bad = process(args[0], check)
    if bad and check:
        print("Нарушен порядок строк (Windows не загрузит): " + ", ".join(bad), file=sys.stderr)
        sys.exit(1)
    if bad:
        print("Порядок строк восстановлен: " + ", ".join(bad))


if __name__ == "__main__":
    main()
