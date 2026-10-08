# -*- coding: utf-8 -*-
"""
砚湖连 YanhuLink —— 应用图标生成脚本

设计思路（三字品牌名的视觉翻译）：
  · 砚   —— 圆角方形的「砚台」底座，沉稳的书卷气
  · 湖   —— 底座里三层水波，取自高邮湖的水面
  · 连   —— 水波上升起连接弧线，象征把设备和校园网连起来

配色沿用工程的品牌蓝渐变（#3B82F6 → #2563EB → #1E40AF），
保证图标与界面「同源」，不会出现两套视觉语言。

输出：
  src/CampusNetLogin/Assets/app.png          1024x1024 主图（README / 关于页用）
  src/CampusNetLogin/Assets/app.ico          16/24/32/48/64/128/256 七层
  docs/icon-preview.png                      各尺寸预览（人工核对用）
"""

from __future__ import annotations

import struct
from pathlib import Path
from PIL import Image, ImageDraw

# ----------------------------------------------------------------------
# 路径
# ----------------------------------------------------------------------
ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "CampusNetLogin" / "Assets"
DOCS = ROOT / "docs"

# ----------------------------------------------------------------------
# 配色
# ----------------------------------------------------------------------
C_TOP = (59, 130, 246)      # #3B82F6 亮蓝
C_MID = (37, 99, 235)       # #2563EB 品牌主色
C_BOT = (30, 64, 175)       # #1E40AF 深蓝

S = 1024                    # 超采样基准画布


def lerp(a: tuple[int, int, int], b: tuple[int, int, int], t: float):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def diagonal_gradient(size: int) -> Image.Image:
    """沿左上→右下方向铺设品牌渐变，与 XAML 的 BrandGradientBrush 一致。"""
    img = Image.new("RGB", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2.0 * (size - 1))          # 0..1
            if t < 0.55:
                c = lerp(C_TOP, C_MID, t / 0.55)
            else:
                c = lerp(C_MID, C_BOT, (t - 0.55) / 0.45)
            px[x, y] = c
    return img


def rounded_mask(size: int, radius_ratio: float = 0.225) -> Image.Image:
    """Windows 11 风格圆角方形遮罩。"""
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    r = int(size * radius_ratio)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=r, fill=255)
    return m


def build_base() -> Image.Image:
    """圆角 + 渐变 的底座（RGBA）。"""
    grad = diagonal_gradient(S).convert("RGBA")
    grad.putalpha(rounded_mask(S))
    return grad


def draw_waves(img: Image.Image) -> None:
    """在底座下半部画三层水波。越靠下越亮，形成「湖光」的层次感。"""
    overlay = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(overlay)

    # (基线 y 比例, 振幅比例, 波长比例, 透明度, 颜色)
    waves = [
        (0.615, 0.052, 0.92, 62, (255, 255, 255)),
        (0.700, 0.045, 0.74, 96, (255, 255, 255)),
        (0.783, 0.038, 0.60, 140, (255, 255, 255)),
    ]
    for base_r, amp_r, wl_r, alpha, color in waves:
        base_y = S * base_r
        amp = S * amp_r
        wl = S * wl_r
        pts = []
        step = 2
        for x in range(-step, S + step * 2, step):
            # 单周期正弦，保证波形自然衔接
            import math
            y = base_y + amp * math.sin(2 * math.pi * (x / wl)) - amp
            pts.append((x, y))
        # 封闭到底部，形成色块
        pts.append((S + step * 2, S))
        pts.append((-step, S))
        d.polygon(pts, fill=color + (alpha,))

    # 遮罩回圆角，防止水波溢出底座
    overlay.putalpha(Image.composite(overlay.getchannel("A"),
                                     Image.new("L", (S, S), 0),
                                     rounded_mask(S)))
    img.alpha_composite(overlay)


def draw_link(img: Image.Image) -> None:
    """
    水波之上画一颗水滴。

    为什么是水滴（而不是 WiFi 扇形）：
      · WiFi 弧线是「外来符号」，硬贴在水波上会有拼接感；
      · 水滴与三层水波同属「水」的视觉体系，天生一体；
      · 水滴在 16px 缩略图下轮廓依然完整，适合任务栏与托盘。
    语义上「水滴落进湖里」，也呼应「砚湖连」的湖字。
    """
    overlay = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(overlay)

    cx = S * 0.5
    cy = S * 0.520          # 抬高一点，让水滴和水波留出呼吸感，不糊在一起
    r = S * 0.138

    # 水滴 = 圆（下半） + 三角（上半尖角）
    d.ellipse([cx - r, cy - r * 0.86, cx + r, cy + r * 1.14],
              fill=(255, 255, 255, 246))
    d.polygon([(cx, cy - r * 1.95),
               (cx - r * 0.86, cy - r * 0.30),
               (cx + r * 0.86, cy - r * 0.30)],
              fill=(255, 255, 255, 246))

    # 高光：居中偏上挖一个小圆孔，体现体积感，避免整块死白。
    # 位置要正，偏左会显得水滴是歪的。
    hr = r * 0.27
    hx, hy = cx, cy + r * 0.16
    d.ellipse([hx - hr, hy - hr, hx + hr, hy + hr],
              fill=(37, 99, 235, 200))

    img.alpha_composite(overlay)


def build_master() -> Image.Image:
    base = build_base()
    draw_waves(base)
    draw_link(base)
    return base


def make_png(master: Image.Image, size: int) -> Image.Image:
    return master.resize((size, size), Image.LANCZOS)


def write_ico(master: Image.Image, path: Path) -> None:
    """
    手写 ICO，避免 Pillow 的 ico 保存器对 256 层做 PNG 压缩时
    丢失部分尺寸。这里显式输出 BMP(DIB) 格式的各层，兼容性最好。
    """
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = [make_png(master, s) for s in sizes]

    entries = []          # (size, payload bytes)
    for im in images:
        w, h = im.size
        # 生成 BMP 数据：BITMAPINFOHEADER(40) + BGRA 像素（自下而上）+ AND 掩码
        px = im.convert("RGBA").load()
        header = struct.pack(
            "<IiiHHIIiiII",
            40,          # biSize
            w,           # biWidth
            h * 2,       # biHeight（含掩码，故为 2 倍）
            1,           # biPlanes
            32,          # biBitCount
            0,           # biCompression = BI_RGB
            w * h * 4,   # biSizeImage
            0, 0, 0, 0,
        )
        body = bytearray()
        for y in range(h - 1, -1, -1):          # 自下而上
            for x in range(w):
                r, g, b, a = px[x, y]
                body += bytes((b, g, r, a))     # BGRA
        # AND 掩码：32bpp 下不生效，但仍需占位且按 4 字节对齐
        mask_row = ((w + 31) // 32) * 4
        body += bytes(mask_row * h)
        entries.append((w, header + bytes(body)))

    # 组装 ICO
    count = len(entries)
    offset = 6 + 16 * count
    directory = bytearray()
    payload = bytearray()
    for w, data in entries:
        directory += struct.pack(
            "<BBBBHHII",
            w if w < 256 else 0,
            w if w < 256 else 0,
            0, 0, 1, 32,
            len(data),
            offset,
        )
        payload += data
        offset += len(data)

    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, count))
        f.write(directory)
        f.write(payload)


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    DOCS.mkdir(parents=True, exist_ok=True)

    master = build_master()
    master.save(ASSETS / "app.png", "PNG")
    write_ico(master, ASSETS / "app.ico")

    # 预览图：一排尺寸 + 大图
    preview = Image.new("RGBA", (1024, 380), (245, 247, 250, 255))
    big = make_png(master, 280)
    preview.alpha_composite(big, (40, 50))
    x = 380
    for s in (256, 128, 64, 48, 32, 24, 16):
        im = make_png(master, s)
        preview.alpha_composite(im, (x, 50 + (280 - s) // 2))
        x += s + 22
    preview.save(DOCS / "icon-preview.png", "PNG")

    print("app.png :", (ASSETS / "app.png").stat().st_size, "bytes")
    print("app.ico :", (ASSETS / "app.ico").stat().st_size, "bytes")
    print("preview :", DOCS / "icon-preview.png")


if __name__ == "__main__":
    main()
