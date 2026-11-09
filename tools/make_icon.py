"""生成 app.ico —— 一个"带折角的文档 + 文字行"图标，16~256 多尺寸。

用法: python tools/make_icon.py
输出: TxtLiveSync.Wpf/app.ico
"""
import pathlib
from PIL import Image, ImageDraw

HERE = pathlib.Path(__file__).resolve().parent
OUT = HERE.parent / "TxtLiveSync.Wpf" / "app.ico"

S = 1024  # 先大图再降采样，边缘干净


def rounded(draw, box, radius, fill):
    draw.rounded_rectangle(box, radius=radius, fill=fill)


def make() -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # 底板：圆角方块，顶部蓝 → 底部青的近渐变（用横条模拟）
    pad = int(S * 0.06)
    top = (0x1F, 0x6F, 0xC4)
    bot = (0x17, 0xA2, 0x9A)
    for i in range(S):
        t = i / (S - 1)
        c = tuple(int(top[k] + (bot[k] - top[k]) * t) for k in range(3)) + (255,)
        d.line([(pad, i), (S - pad, i)], fill=c)
    # 把底板裁成圆角：用遮罩
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (pad, pad, S - pad, S - pad), radius=int(S * 0.19), fill=255
    )
    plate = img.copy()
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    img.paste(plate, (0, 0), mask)
    d = ImageDraw.Draw(img)

    # 纸张：白色圆角矩形，右上角折角
    px0, py0 = int(S * 0.28), int(S * 0.22)
    px1, py1 = int(S * 0.72), int(S * 0.79)
    fold = int(S * 0.13)
    paper = (255, 255, 255, 255)
    d.polygon(
        [
            (px0, py0), (px1 - fold, py0), (px1, py0 + fold),
            (px1, py1), (px0, py1),
        ],
        fill=paper,
    )
    # 折角阴影
    d.polygon(
        [(px1 - fold, py0), (px1, py0 + fold), (px1 - fold, py0 + fold)],
        fill=(0xD8, 0xE3, 0xEE, 255),
    )

    # 正文文字行
    line_c = (0x3A, 0x7E, 0xBB, 255)
    x0 = px0 + int(S * 0.07)
    x1 = px1 - int(S * 0.07)
    y = py0 + int(S * 0.19)
    step = int(S * 0.098)
    th = int(S * 0.045)
    for i in range(4):
        w = x1 if i < 3 else x0 + int((x1 - x0) * 0.58)
        d.rounded_rectangle((x0, y, w, y + th), radius=th // 2, fill=line_c)
        y += step

    return img


def main() -> None:
    big = make()
    sizes = [16, 24, 32, 48, 64, 128, 256]
    OUT.parent.mkdir(parents=True, exist_ok=True)
    big.save(OUT, format="ICO", sizes=[(s, s) for s in sizes])
    # 顺手存一张预览图，方便看效果
    big.resize((256, 256), Image.LANCZOS).save(HERE.parent / "icon_preview.png")
    print(f"icon -> {OUT} ({OUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
