# -*- coding: utf-8 -*-
"""
图标符号方案对比 —— 只生成预览图，不改动正式资源。

背景：第一版用的「三条同心圆弧」（WiFi 扇形）用户反馈「融入得不好看」。
本脚本给出 6 个替代方案，全部保持「水波 + 一个符号」的结构，
只换中间那个符号，方便横向对比。

运行：python tools/icon_variants.py
产出：docs/icon-variants.png
"""

from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
DOCS = ROOT / "docs"

C_TOP = (59, 130, 246)
C_MID = (37, 99, 235)
C_BOT = (30, 64, 175)

S = 1024


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def gradient(size=S):
    img = Image.new("RGB", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2.0 * (size - 1))
            c = lerp(C_TOP, C_MID, t / 0.55) if t < 0.55 else lerp(C_MID, C_BOT, (t - 0.55) / 0.45)
            px[x, y] = c
    return img


def rounded(size=S, ratio=0.225):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, size - 1, size - 1], radius=int(size * ratio), fill=255)
    return m


def base():
    g = gradient().convert("RGBA")
    g.putalpha(rounded())
    return g


def waves(img):
    ov = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(ov)
    for base_r, amp_r, wl_r, alpha in [
        (0.615, 0.052, 0.92, 62),
        (0.700, 0.045, 0.74, 96),
        (0.783, 0.038, 0.60, 140),
    ]:
        by, amp, wl = S * base_r, S * amp_r, S * wl_r
        pts = []
        for x in range(-4, S + 8, 2):
            y = by + amp * math.sin(2 * math.pi * (x / wl)) - amp
            pts.append((x, y))
        pts += [(S + 8, S), (-4, S)]
        d.polygon(pts, fill=(255, 255, 255, alpha))
    ov.putalpha(Image.composite(ov.getchannel("A"), Image.new("L", (S, S), 0), rounded()))
    img.alpha_composite(ov)


def white_layer():
    ov = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    return ov, ImageDraw.Draw(ov)


# ---------------------------------------------------------------- 方案 A
def sym_chevrons():
    """单个向上的「人」字箭头 —— 简洁，位置安全。"""
    ov, d = white_layer()
    w = int(S * 0.062)
    cy, half, h = S * 0.565, S * 0.26, S * 0.19
    d.line([(S / 2 - half, cy + h / 2), (S / 2, cy - h / 2), (S / 2 + half, cy + h / 2)],
           fill=(255, 255, 255, 242), width=w, joint="curve")
    return ov


# ---------------------------------------------------------------- 方案 G
def sym_leaf():
    """水波上的两片叶子 —— 校园 + 生机，形状与波浪呼应。"""
    ov, d = white_layer()
    cx, cy = S / 2, S * 0.545
    rx, ry = S * 0.175, S * 0.235
    # 左叶
    d.ellipse([cx - rx * 1.75, cy - ry, cx - rx * 0.15, cy + ry], fill=(255, 255, 255, 236))
    # 右叶
    d.ellipse([cx + rx * 0.15, cy - ry, cx + rx * 1.75, cy + ry], fill=(255, 255, 255, 208))
    # 中缝镂空
    d.line([(cx, cy - ry * 1.02), (cx, cy + ry * 1.02)], fill=(37, 99, 235, 230), width=int(S * 0.024))
    return ov


# ---------------------------------------------------------------- 方案 H
def sym_globe():
    """水波上的经线球 —— 「联网」的通用符号，但不带 WiFi 扇形感。"""
    ov, d = white_layer()
    cx, cy, r = S / 2, S * 0.545, S * 0.185
    lw = int(S * 0.046)
    d.ellipse([cx - r, cy - r, cx + r, cy + r], outline=(255, 255, 255, 235), width=lw)
    # 两条经线（竖椭圆）
    for k in (0.52, 0.86):
        d.ellipse([cx - r * k, cy - r, cx + r * k, cy + r], outline=(255, 255, 255, 175), width=int(lw * 0.72))
    # 赤道
    d.line([(cx - r, cy), (cx + r, cy)], fill=(255, 255, 255, 190), width=int(lw * 0.72))
    return ov


# ---------------------------------------------------------------- 方案 B
def sym_drop():
    """水波之上悬一颗水滴 —— 呼应「湖」，且与波浪同源，最自然。"""
    ov, d = white_layer()
    cx, cy = S / 2, S * 0.575
    r = S * 0.145
    # 水滴 = 圆 + 上方尖角
    d.ellipse([cx - r, cy - r * 0.86, cx + r, cy + r * 1.14], fill=(255, 255, 255, 246))
    d.polygon([(cx, cy - r * 1.95), (cx - r * 0.86, cy - r * 0.30), (cx + r * 0.86, cy - r * 0.30)],
              fill=(255, 255, 255, 246))
    # 镂空一点高光
    hr = r * 0.30
    d.ellipse([cx - r * 0.42 - hr, cy + r * 0.06 - hr, cx - r * 0.42 + hr, cy + r * 0.06 + hr],
              fill=(37, 99, 235, 210))
    return ov


# ---------------------------------------------------------------- 方案 C
def sym_nodes():
    """三点连成折线 —— 「连」最直白的表达，几何感强。"""
    ov, d = white_layer()
    pts = [(S * 0.30, S * 0.66), (S * 0.50, S * 0.45), (S * 0.70, S * 0.66)]
    d.line(pts, fill=(255, 255, 255, 190), width=int(S * 0.038), joint="curve")
    for (x, y), a in zip(pts, (255, 255, 255)):
        r = S * 0.062
        d.ellipse([x - r, y - r, x + r, y + r], fill=(255, 255, 255, a))
    return ov


# ---------------------------------------------------------------- 方案 D
def sym_bridge():
    """水上一座单拱桥 —— 校园气最足，但语义稍隐晦。"""
    ov, d = white_layer()
    cx, cy = S / 2, S * 0.755
    r = S * 0.24
    d.arc([cx - r, cy - r, cx + r, cy + r], start=180, end=360,
          fill=(255, 255, 255, 240), width=int(S * 0.055))
    d.line([(cx - r * 1.30, cy), (cx + r * 1.30, cy)], fill=(255, 255, 255, 205), width=int(S * 0.042))
    return ov


# ---------------------------------------------------------------- 方案 E
def sym_teardrop_ring():
    """水波上的「跳动的圆环 + 中心点」—— 最简约，小尺寸也清晰。"""
    ov, d = white_layer()
    cx, cy = S / 2, S * 0.545
    r = S * 0.165
    d.ellipse([cx - r, cy - r, cx + r, cy + r], outline=(255, 255, 255, 235), width=int(S * 0.052))
    ir = S * 0.052
    d.ellipse([cx - ir, cy - ir, cx + ir, cy + ir], fill=(255, 255, 255, 250))
    return ov


# ---------------------------------------------------------------- 方案 F
def sym_none():
    """纯水波，不加任何符号 —— 最干净，品牌识别靠形状与配色。"""
    return None


VARIANTS = [
    ("A 单箭头", sym_chevrons),
    ("B 水滴", sym_drop),
    ("C 三点连线", sym_nodes),
    ("D 拱桥", sym_bridge),
    ("E 圆环点", sym_teardrop_ring),
    ("F 纯水波", sym_none),
    ("G 双叶", sym_leaf),
    ("H 经纬球", sym_globe),
]


def build(sym_fn):
    img = base()
    waves(img)
    if sym_fn is not None:
        ov = sym_fn()
        if ov is not None:
            img.alpha_composite(ov)
    return img


def main():
    DOCS.mkdir(parents=True, exist_ok=True)
    cols, cell, pad = 8, 250, 22
    small = 84
    W = pad + cols * (cell + pad)
    H = pad + cell + 60 + small + pad
    sheet = Image.new("RGBA", (W, H), (247, 248, 250, 255))

    d = ImageDraw.Draw(sheet)
    x = pad
    for label, fn in VARIANTS:
        big = build(fn).resize((cell, cell), Image.LANCZOS)
        sheet.alpha_composite(big, (x, pad))
        # 一排小尺寸，检查缩小后是否还可辨认
        sx = x
        for s in (48, 32, 24, 16):
            im = build(fn).resize((s, s), Image.LANCZOS)
            sheet.alpha_composite(im, (sx, pad + cell + 18 + (small - s) // 2))
            sx += s + 9
        d.text((x + 4, pad + cell + 46), label, fill=(60, 64, 72, 255))
        x += cell + pad

    sheet.save(DOCS / "icon-variants.png")
    print("saved", DOCS / "icon-variants.png")


if __name__ == "__main__":
    main()
