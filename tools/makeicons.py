"""Disegna le icone di DaProd Server e DaProd Launcher (ico multi-risoluzione). Uso: python tools/makeicons.py"""
import math, os
from PIL import Image, ImageDraw, ImageFilter

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
S = 1024  # disegno grande, poi ridotto


def gradient(c1, c2, angle=90):
    im = Image.new("RGB", (S, S))
    px = im.load()
    a = math.radians(angle)
    dx, dy = math.cos(a), math.sin(a)
    for y in range(S):
        for x in range(S):
            t = ((x - S / 2) * dx + (y - S / 2) * dy) / S + 0.5
            t = min(1, max(0, t))
            px[x, y] = tuple(int(c1[i] + (c2[i] - c1[i]) * t) for i in range(3))
    return im


def rounded_mask(r=210, inset=24):
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).rounded_rectangle([inset, inset, S - inset, S - inset], r, fill=255)
    return m


def glow(im, color, box_fn, blur=30, alpha=170):
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    box_fn(ImageDraw.Draw(layer), color + (alpha,))
    layer = layer.filter(ImageFilter.GaussianBlur(blur))
    im.alpha_composite(layer)


def finish(im, name, out):
    sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)]
    big = im.resize((256, 256), Image.LANCZOS)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    big.save(out, format="ICO", sizes=sizes)
    im.resize((512, 512), Image.LANCZOS).save(out.replace(".ico", ".png"))
    print("scritto", out)


def base(c1, c2):
    bg = gradient(c1, c2, 60).convert("RGBA")
    out = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    out.paste(bg, (0, 0), rounded_mask())
    # bordo luminoso
    d = ImageDraw.Draw(out)
    d.rounded_rectangle([24, 24, S - 24, S - 24], 210, outline=(255, 255, 255, 60), width=10)
    return out


def launcher():
    im = base((36, 20, 92), (12, 130, 160))
    d = ImageDraw.Draw(im)
    # anello
    glow(im, (120, 220, 255), lambda dd, c: dd.ellipse([196, 196, 828, 828], outline=c, width=34), 26, 200)
    d = ImageDraw.Draw(im)
    d.ellipse([196, 196, 828, 828], outline=(235, 250, 255, 255), width=30)
    d.ellipse([250, 250, 774, 774], outline=(255, 255, 255, 70), width=8)
    # freccia "play" dorata
    tri = [(400, 330), (400, 694), (720, 512)]
    glow(im, (255, 190, 60), lambda dd, c: dd.polygon(tri, fill=c), 30, 220)
    d = ImageDraw.Draw(im)
    tri_img = gradient((255, 214, 90), (255, 140, 30), 90)
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).polygon(tri, fill=255)
    m = m.filter(ImageFilter.GaussianBlur(2))
    im.paste(tri_img, (0, 0), m)
    # scintille
    for (cx, cy, r) in [(760, 270, 34), (300, 740, 22)]:
        d.polygon([(cx, cy - r * 2), (cx + r * .5, cy - r * .5), (cx + r * 2, cy), (cx + r * .5, cy + r * .5), (cx, cy + r * 2), (cx - r * .5, cy + r * .5), (cx - r * 2, cy), (cx - r * .5, cy - r * .5)], fill=(255, 255, 255, 235))
    finish(im, "launcher", os.path.join(BASE, "src", "DaProd.Launcher", "Assets", "launcher.ico"))


def server():
    im = base((22, 24, 40), (78, 40, 120))
    d = ImageDraw.Draw(im)
    # scudo
    shield = [(512, 150), (800, 250), (800, 540), (512, 880), (224, 540), (224, 250)]
    glow(im, (255, 170, 60), lambda dd, c: dd.polygon(shield, outline=c, fill=None, width=30), 30, 230)
    d = ImageDraw.Draw(im)
    fill = gradient((44, 48, 80), (24, 26, 48), 90)
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).polygon(shield, fill=255)
    im.paste(fill, (0, 0), m)
    d.line(shield + [shield[0]], fill=(255, 196, 90, 255), width=26, joint="curve")
    # tre "server" impilati
    for i, y in enumerate((300, 436, 572)):
        bar = [326, y, 698, y + 106]
        d.rounded_rectangle(bar, 30, fill=(70, 130, 255, 255) if i != 1 else (255, 176, 60, 255))
        d.rounded_rectangle([bar[0] + 8, bar[1] + 8, bar[2] - 8, bar[3] - 8], 24, outline=(255, 255, 255, 90), width=6)
        d.ellipse([bar[0] + 30, y + 35, bar[0] + 64, y + 69], fill=(120, 255, 170, 255))
        d.rounded_rectangle([bar[0] + 96, y + 43, bar[0] + 290, y + 62], 10, fill=(255, 255, 255, 120))
    finish(im, "server", os.path.join(BASE, "src", "DaProd.ServerPanel", "Assets", "server.ico"))


if __name__ == "__main__":
    launcher()
    server()
