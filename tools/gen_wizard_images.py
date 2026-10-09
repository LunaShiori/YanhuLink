#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成 Inno Setup 安装向导的品牌图片。

背景：安装包此前**没有**配置 WizardImageFile / WizardSmallImageFile，
所以安装向导里显示的是 Inno Setup 自带的默认图（左下角大图和右上角小图），
用户会觉得「安装包没有应用图标」。

本脚本从品牌图标 app.png 生成两张图：
  · installer/assets/wizard-small.bmp  55x55    向导右上角小图
  · installer/assets/wizard-large.bmp  164x314  向导左侧大图

改设计只需改这里，可重复运行。
"""
import os
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC_ICON = os.path.join(ROOT, "src", "CampusNetLogin", "Assets", "app.png")
OUT_DIR = os.path.join(ROOT, "installer", "assets")

# 与 Styles/Theme.xaml 的 BrandGradientBrush 同源
BRAND_TOP = (30, 64, 175)      # #1E40AF
BRAND_BOTTOM = (59, 130, 246)  # #3B82F6
BRAND_MID = (37, 99, 235)      # #2563EB


def load_icon(size):
    """读入方形品牌图标并缩放到 size×size（保留透明通道）。"""
    img = Image.open(SRC_ICON).convert("RGBA")
    return img.resize((size, size), Image.LANCZOS)


def vertical_gradient(w, h, top, bottom):
    """生成竖直渐变底。"""
    grad = Image.new("RGB", (1, h))
    for y in range(h):
        t = y / max(1, h - 1)
        grad.putpixel((0, y), (
            int(top[0] + (bottom[0] - top[0]) * t),
            int(top[1] + (bottom[1] - top[1]) * t),
            int(top[2] + (bottom[2] - top[2]) * t),
        ))
    return grad.resize((w, h), Image.BILINEAR).convert("RGBA")


def find_font(size):
    """找一个能显示中文的系统字体。"""
    for name in ("msyh.ttc", "msyhbd.ttc", "simhei.ttf", "simsun.ttc"):
        p = os.path.join(r"C:\Windows\Fonts", name)
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, size)
            except Exception:
                pass
    return ImageFont.load_default()


def make_small(path):
    """向导右上角小图：白底 + 品牌图标，55x55。"""
    size = 55
    canvas = Image.new("RGBA", (size, size), (255, 255, 255, 255))
    icon = load_icon(size - 6)
    canvas.paste(icon, (3, 3), icon)
    canvas.convert("RGB").save(path, "BMP")
    print("written %s  %dx%d" % (path, size, size))


def make_large(path):
    """向导左侧大图：品牌渐变 + 图标 + 名称，164x314。"""
    w, h = 164, 314
    canvas = vertical_gradient(w, h, BRAND_TOP, BRAND_BOTTOM)

    # 中上部的浅色圆角卡片，把图标衬出来
    card = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(card)
    card_w, card_h = 106, 106
    cx, cy = w // 2, 96
    d.rounded_rectangle(
        [cx - card_w // 2, cy - card_h // 2, cx + card_w // 2, cy + card_h // 2],
        radius=26, fill=(255, 255, 255, 46))
    canvas = Image.alpha_composite(canvas, card)

    icon = load_icon(86)
    canvas.paste(icon, (cx - 43, cy - 43), icon)

    d = ImageDraw.Draw(canvas)
    f_title = find_font(23)
    f_sub = find_font(12)
    f_tip = find_font(10)

    # 中文名
    d.text((cx, 178), "砚湖连", font=f_title, fill=(255, 255, 255, 255), anchor="mm")
    # 英文名
    d.text((cx, 202), "YanhuLink", font=f_sub, fill=(219, 234, 254, 255), anchor="mm")

    # 分隔线
    d.line([(cx - 34, 222), (cx + 34, 222)], fill=(255, 255, 255, 70), width=1)

    # 底部说明
    for i, line in enumerate(("校园网自动连接工具", "扬州职业技术大学", "高邮湖校区")):
        d.text((cx, 244 + i * 17), line, font=f_tip,
               fill=(226, 238, 255, 235), anchor="mm")

    canvas.convert("RGB").save(path, "BMP")
    print("written %s  %dx%d" % (path, w, h))


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    if not os.path.exists(SRC_ICON):
        raise SystemExit("找不到源图标: " + SRC_ICON)
    make_small(os.path.join(OUT_DIR, "wizard-small.bmp"))
    make_large(os.path.join(OUT_DIR, "wizard-large.bmp"))


if __name__ == "__main__":
    main()
