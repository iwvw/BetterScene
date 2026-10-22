"""
从原始素材生成托盘图标（TrayWhite.ico / TrayBlack.ico）。

不做任何手工缩放或后处理，直接把 128px 原图交给 Pillow 的 ICO 编码器，
由它按各尺寸重采样。这样既忠实于原素材，边缘也由成熟的图像库处理，
不会出现自己实现的降采样带来的阶梯或灰边。
"""

from PIL import Image

SRC_WHITE = r'E:\图片\图标_bs\#ffffff.png'
SRC_BLACK = r'E:\图片\图标_bs\#000000.png'
OUT_DIR = r'E:\Code\BetterScene\src\BetterScene.App\Assets'

# Windows 按 SM_CXSMICON 取图：100% = 16，125% = 20，150% = 24，200% = 32
SIZES = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]


def build(src_path: str, out_path: str):
    src = Image.open(src_path).convert('RGBA')
    src.save(out_path, format='ICO', sizes=SIZES)

    probe = Image.open(out_path)
    print(f'{out_path}  sizes={sorted(probe.ico.sizes())}')


def main():
    build(SRC_WHITE, OUT_DIR + r'\TrayWhite.ico')
    build(SRC_BLACK, OUT_DIR + r'\TrayBlack.ico')


if __name__ == '__main__':
    main()
