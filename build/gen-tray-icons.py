"""
从原始素材生成托盘图标（TrayWhite.ico / TrayBlack.ico）。

只做缩放，不改形状 —— 所有尺寸都由 E:\图片\图标_bs 里的 128px 原图得出，
没有重画任何笔画。

小尺寸的难点不是"缩放算法"，而是"细笔画会被缩没"：
原图地平线 4px 宽，缩到 24px 只剩 0.75px，alpha 只有 75%，
渲染出来是一条灰线；和系统自带图标（纯黑/纯白实心）并排就显脏、显糊。

处理办法是把 alpha 二值化：>= 阈值的定为全不透明，其余全透明。
轮廓完全由原图决定，只是让每条笔画落成实心像素。
阈值取 80（0-255），能保住 4px 地平线，又不会把背景抗锯齿吃进来。
"""

import io
import struct
from PIL import Image

SRC_WHITE = r'E:\图片\图标_bs\#ffffff.png'
SRC_BLACK = r'E:\图片\图标_bs\#000000.png'
OUT_DIR = r'E:\Code\BetterScene\src\BetterScene.App\Assets'

# Windows 按 SM_CXSMICON 取图：100% = 16，125% = 20，150% = 24，200% = 32
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)

# 这些尺寸以下做二值化保笔画；更大的尺寸细节够，直接缩放。
CRISP_BELOW = 64

# 二值化阈值（0-255）。原图笔画主体是 255，抗锯齿边缘 30~220。
# 80 能保住 4px 宽的地平线，又不会把背景噪点吃进来。
CRISP_THRESHOLD = 80


def downscale(alpha: Image.Image, size: int) -> Image.Image:
    """缩放 alpha，小尺寸做二值化以保住细笔画。"""
    small = alpha.resize((size, size), Image.LANCZOS)

    if size >= CRISP_BELOW:
        return small

    return small.point(lambda v: 255 if v >= CRISP_THRESHOLD else 0)


def render(src: Image.Image, size: int, color: tuple[int, int, int]) -> Image.Image:
    """
    生成指定尺寸的一帧。

    只缩放 alpha，RGB 用固定纯色填充。
    白色图标如果在透明背景上直接做 RGBA 缩放，
    透明像素的 RGB(0,0,0) 会参与加权，边缘出现一圈灰边
    （黑色图标相反，是亮边）。分开处理就没这个问题。
    """
    alpha = src.convert('RGBA').getchannel('A')
    out = Image.new('RGBA', (size, size), color + (0,))
    out.putalpha(downscale(alpha, size))
    return out


def write_ico(frames, path: str):
    """
    自己拼 ICO 容器。

    PIL 的 ICO 保存器只会把第一张图逐级缩小，
    没法给不同尺寸塞不同的处理，所以手动写目录项。
    每帧用 PNG 压缩（Vista 之后都支持）。
    """
    blobs = []
    for size, im in frames:
        buf = io.BytesIO()
        im.save(buf, format='PNG')
        blobs.append((size, buf.getvalue()))

    header = struct.pack('<HHH', 0, 1, len(blobs))
    offset = len(header) + 16 * len(blobs)
    entries = b''
    data = b''

    for size, blob in blobs:
        dim = 0 if size >= 256 else size
        entries += struct.pack('<BBBBHHII', dim, dim, 0, 0, 1, 32, len(blob), offset)
        data += blob
        offset += len(blob)

    with open(path, 'wb') as f:
        f.write(header + entries + data)


def build(src_path: str, out_path: str):
    src = Image.open(src_path).convert('RGBA')
    color = src.getpixel((64, 60))[:3]

    frames = [(size, render(src, size, color)) for size in SIZES]
    write_ico(frames, out_path)

    probe = Image.open(out_path)
    print(f'{out_path}  color={color}  sizes={sorted(probe.ico.sizes())}')


def main():
    build(SRC_WHITE, OUT_DIR + r'\TrayWhite.ico')
    build(SRC_BLACK, OUT_DIR + r'\TrayBlack.ico')


if __name__ == '__main__':
    main()
