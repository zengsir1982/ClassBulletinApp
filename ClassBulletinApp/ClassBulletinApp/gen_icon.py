import os, struct
from PIL import Image, ImageDraw, ImageFont

OUT = r"F:/study/班级公布栏/ClassBulletinApp/ClassBulletinApp/Assets/AppIcon.ico"
FONT = r"C:/Windows/Fonts/simhei.ttf"
SIZES = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]

TOP = (70, 130, 220)
BOTTOM = (35, 92, 190)
WHITE = (255, 255, 255, 255)


def make(size):
    w, h = size
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    r = max(2, int(w * 0.18))
    d.rounded_rectangle([0, 0, w - 1, h - 1], radius=r, fill=BOTTOM)
    d.rounded_rectangle([0, 0, w - 1, int(h * 0.5)], radius=r, fill=TOP)
    d.rounded_rectangle([0, 0, w - 1, h - 1], radius=r, outline=(255, 255, 255, 70), width=max(1, int(w * 0.03)))
    fs = int(h * 0.66)
    try:
        fnt = ImageFont.truetype(FONT, fs)
    except Exception:
        fnt = ImageFont.load_default()
    bbox = d.textbbox((0, 0), "班", font=fnt)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    x = (w - tw) / 2 - bbox[0]
    y = (h - th) / 2 - bbox[1] - int(h * 0.02)
    d.text((x, y), "班", font=fnt, fill=WHITE)
    return img


def bmp_frame(img):
    w, h = img.size
    # 取 RGBA 像素，转 BGRA，并上下翻转（BMP 自底向上）
    px = img.tobytes()  # RGBA, top-down
    # 转 BGRA
    rgba = bytearray(px)
    for i in range(0, len(rgba), 4):
        r, g, b, a = rgba[i], rgba[i+1], rgba[i+2], rgba[i+3]
        rgba[i], rgba[i+1], rgba[i+2], rgba[i+3] = b, g, r, a
    # 翻转行
    row = w * 4
    lines = [rgba[i:i+row] for i in range(0, len(rgba), row)]
    lines.reverse()
    xor = b"".join(lines)
    # AND 掩码：全 0（透明度由 alpha 字节处理）
    and_row = ((w + 31) // 32) * 4
    and_mask = b"\x00" * (and_row * h)
    bih = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    return bih + xor + and_mask


os.makedirs(os.path.dirname(OUT), exist_ok=True)
frames = [bmp_frame(make(s)) for s in SIZES]

header = struct.pack("<HHH", 0, 1, len(frames))
entries = b""
data = b""
offset = 6 + 16 * len(frames)
for (w, h), f in zip(SIZES, frames):
    bw = 0 if w == 256 else w
    bh = 0 if h == 256 else h
    entries += struct.pack("<BBBBHHII", bw, bh, 0, 0, 1, 32, len(f), offset)
    data += f
    offset += len(f)

ico = header + entries + data
with open(OUT, "wb") as fh:
    fh.write(ico)
print("saved", OUT, os.path.getsize(OUT), "bytes")

# 验证：逐帧解析
with open(OUT, "rb") as fh:
    raw = fh.read()
    cnt = struct.unpack("<HHH", raw[:6])[2]
    print("entries", cnt)
    for i in range(cnt):
        e = raw[6 + 16*i: 6 + 16*(i+1)]
        bw, bh = e[0], e[1]
        w = 256 if bw == 0 else bw
        h = 256 if bh == 0 else bh
        sz = struct.unpack("<I", e[8:12])[0]
        print("frame", i, w, "x", h, sz, "bytes")
