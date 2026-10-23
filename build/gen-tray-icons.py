"""
从原始素材生成托盘图标（TrayWhite.ico / TrayBlack.ico）。

处理流程：
1. 按 alpha 通道取可见内容的 bbox，裁掉素材四周的空白；
2. 等比缩放到目标画布（保留 6px 安全边距），居中放置；
3. 交给 Pillow 的 ICO 编码器按各尺寸重采样。

为什么要做裁边居中：
原始素材是 128x128，但可见内容只有 108x100，而且留白不对称
（上 10px、下 18px、左右各 10px）。多出来的 8px 底部空白会让图形
在托盘里看起来偏上、偏瘦。裁紧后重新居中，24px 档的可见内容
从 20x19 变成 22x20，上下留白也从 2/3 修正成对称的 2/2。

为什么保留 6px 边距而不是更小：
图形底部有三条横杠，最下面那条极细。边距压到 0 虽然能让图形更大，
但在 16px 档（100% 缩放）下那条短杠会和上方横杠糊在一起，
边缘也可能被某些渲染路径裁掉。6px 是实测下来既能明显变大、
又不损失细节的取值。
"""

from PIL import Image

SRC_WHITE = r'E:\图片\图标_bs\#ffffff.png'
SRC_BLACK = r'E:\图片\图标_bs\#000000.png'
OUT_DIR = r'E:\Code\BetterScene\src\BetterScene.App\Assets'

# 最终画布尺寸。素材本身是 128，用同一个尺寸重建，保持原有缩放基准。
CANVAS = 128

# 裁边后内容四周保留的安全边距（画布像素）。
MARGIN = 6

# Windows 按 SM_CXSMICON 取图：100% = 16，125% = 20，150% = 24，200% = 32
SIZES = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]


def normalize(src_path: str) -> Image.Image:
    """裁掉空白、等比缩放、居中放到新画布上。"""
    src = Image.open(src_path).convert('RGBA')

    bbox = src.split()[-1].getbbox()
    if bbox is None:
        raise ValueError(f'{src_path} 完全透明，没有可用的内容')

    content = src.crop(bbox)

    box = CANVAS - MARGIN * 2
    w, h = content.size
    scale = min(box / w, box / h)

    # 用 round 而不是 int：int 会向下取整，在多档尺寸下累积成 1px 的偏差。
    new_w = max(1, round(w * scale))
    new_h = max(1, round(h * scale))

    # LANCZOS：缩小图标时比 BICUBIC 更能保住细笔画的对比度。
    content = content.resize((new_w, new_h), Image.LANCZOS)

    canvas = Image.new('RGBA', (CANVAS, CANVAS), (0, 0, 0, 0))
    canvas.paste(
        content,
        ((CANVAS - new_w) // 2, (CANVAS - new_h) // 2),
        content,
    )

    print(f'  {src_path}')
    print(f'    原内容 {w}x{h} -> {new_w}x{new_h}  (画布 {CANVAS}, 边距 {MARGIN})')
    return canvas


def build(src_path: str, out_path: str):
    icon = normalize(src_path)
    icon.save(out_path, format='ICO', sizes=SIZES)

    probe = Image.open(out_path)
    print(f'    -> {out_path}  sizes={sorted(probe.ico.sizes())}')


def main():
    build(SRC_WHITE, OUT_DIR + r'\TrayWhite.ico')
    build(SRC_BLACK, OUT_DIR + r'\TrayBlack.ico')


if __name__ == '__main__':
    main()
