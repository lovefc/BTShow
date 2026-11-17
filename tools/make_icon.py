#!/usr/bin/env python3
"""
生成 Assets/app.ico —— 一个电池图标，PNG 压缩，多尺寸。

为什么自己生成：
  原来的 app.ico 有 285KB，对于一个目标 1MB 的小工具来说太浪费。
  用 PNG 压缩的纯色图形，4 个尺寸加起来只有几 KB。

用法：python tools/make_icon.py
"""

import os
import struct
import zlib

# 归一化到 32x32 坐标系的电池几何（与 UI/TrayIconPainter.cs 保持一致）
BODY = (6.0, 8.0, 26.0, 28.0)
NUB = (12.0, 4.0, 20.0, 8.0)
INNER = (8.0, 10.0, 24.0, 26.0)
R_BODY, R_NUB, R_INNER = 3.0, 1.0, 2.0

OUTLINE = (85, 85, 85)
FILL = (76, 175, 80)  # 满电绿

SIZES = [16, 32, 48, 64, 256]


def in_rounded(x, y, left, top, right, bottom, radius):
    if x < left or x > right or y < top or y > bottom:
        return False
    dx = 0.0
    dy = 0.0
    if x < left + radius:
        dx = left + radius - x
    elif x > right - radius:
        dx = x - (right - radius)
    if y < top + radius:
        dy = top + radius - y
    elif y > bottom - radius:
        dy = y - (bottom - radius)
    return dx * dx + dy * dy <= radius * radius


def render(size):
    """返回 RGBA 像素行（自上而下），透明背景。"""
    s = size / 32.0
    body = tuple(v * s for v in BODY)
    nub = tuple(v * s for v in NUB)
    inner = tuple(v * s for v in INNER)
    r_body, r_nub, r_inner = R_BODY * s, R_NUB * s, R_INNER * s

    rows = []
    step = 0.5  # 2x2 超采样

    for y in range(size):
        row = bytearray()
        for x in range(size):
            outline = 0
            fill = 0
            for sy in range(2):
                for sx in range(2):
                    px = x + 0.25 + sx * step
                    py = y + 0.25 + sy * step

                    if not in_rounded(px, py, *body, r_body) and not in_rounded(px, py, *nub, r_nub):
                        continue

                    if in_rounded(px, py, *inner, r_inner):
                        fill += 1
                    else:
                        outline += 1

            total = outline + fill
            if total == 0:
                row += bytes((0, 0, 0, 0))
                continue

            color = FILL if fill >= outline else OUTLINE
            row += bytes((color[0], color[1], color[2], total * 255 // 4))

        rows.append(row)

    return rows


def png(width, height, rows):
    raw = b"".join(b"\x00" + bytes(row) for row in rows)

    def chunk(tag, data):
        payload = tag + data
        return struct.pack(">I", len(data)) + payload + struct.pack(">I", zlib.crc32(payload) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", ihdr)
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


def main():
    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "BtBattery", "Assets")
    out_dir = os.path.normpath(out_dir)
    os.makedirs(out_dir, exist_ok=True)

    images = [(size, png(size, size, render(size))) for size in SIZES]

    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)

    entries = b""
    blobs = b""
    for size, data in images:
        entries += struct.pack(
            "<BBBBHHII",
            size % 256,  # 256 用 0 表示
            size % 256,
            0,
            0,
            1,
            32,
            len(data),
            offset,
        )
        blobs += data
        offset += len(data)

    path = os.path.join(out_dir, "app.ico")
    with open(path, "wb") as f:
        f.write(header + entries + blobs)

    print(f"written: {path}  ({os.path.getsize(path)} bytes)")


if __name__ == "__main__":
    main()
