"""Disegna la splash del launcher (Ponticheage): python tools/makesplash.py  ->  src/DaProd.Launcher/Assets/splash.png"""
import math, os, random
from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(BASE, "src", "DaProd.Launcher", "Assets", "splash.png")
W, H, K = 1200, 675, 2          # dimensione finale e supersampling
w, h = W * K, H * K
random.seed(7)


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def vgrad(stops, y0, y1):
    im = Image.new("RGB", (w, y1 - y0))
    px = im.load()
    for y in range(y1 - y0):
        t = y / max(1, y1 - y0 - 1)
        c = stops[-1][1]
        for i in range(len(stops) - 1):
            if stops[i][0] <= t <= stops[i + 1][0]:
                u = (t - stops[i][0]) / (stops[i + 1][0] - stops[i][0])
                c = lerp(stops[i][1], stops[i + 1][1], u)
                break
        for x in range(w):
            px[x, y] = c
    return im


horizon = int(h * 0.60)
img = Image.new("RGB", (w, h))
img.paste(vgrad([(0, (10, 8, 34)), (0.35, (52, 26, 88)), (0.7, (176, 78, 104)), (1, (255, 170, 92))], 0, horizon), (0, 0))
img.paste(vgrad([(0, (255, 150, 96)), (0.08, (120, 54, 100)), (0.4, (34, 28, 70)), (1, (10, 12, 34))], horizon, h), (0, horizon))
img = img.convert("RGBA")

# stelle
st = Image.new("RGBA", (w, h), (0, 0, 0, 0))
sd = ImageDraw.Draw(st)
for _ in range(260):
    x, y = random.randint(0, w), int(random.random() ** 1.6 * horizon * 0.8)
    r = random.choice([1, 1, 2, 2, 3]) * K / 2
    sd.ellipse([x - r, y - r, x + r, y + r], fill=(255, 244, 220, random.randint(90, 230)))
img.alpha_composite(st.filter(ImageFilter.GaussianBlur(0.6 * K)))

# sole con alone
sx, sy, sr = int(w * 0.5), horizon - int(h * 0.15), int(h * 0.08)
glow = Image.new("RGBA", (w, h), (0, 0, 0, 0))
ImageDraw.Draw(glow).ellipse([sx - sr * 3, sy - sr * 3, sx + sr * 3, sy + sr * 3], fill=(255, 150, 80, 120))
img.alpha_composite(glow.filter(ImageFilter.GaussianBlur(sr * 0.9)))
sun = Image.new("RGBA", (w, h), (0, 0, 0, 0))
ImageDraw.Draw(sun).ellipse([sx - sr, sy - sr, sx + sr, sy + sr], fill=(255, 226, 150, 255))
img.alpha_composite(sun.filter(ImageFilter.GaussianBlur(1.5 * K)))


# montagne lontane
def ridge(base_y, amp, col, seed, rough=0.55):
    random.seed(seed)
    pts, y, step = [], 0.0, 18 * K
    for x in range(0, w + step, step):
        y = y * rough + random.uniform(-amp, amp)
        pts.append((x, base_y - abs(y) - amp * 0.6))
    layer = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    ImageDraw.Draw(layer).polygon(pts + [(w, horizon), (0, horizon)], fill=col)
    return layer.filter(ImageFilter.GaussianBlur(0.8 * K))


img.alpha_composite(ridge(horizon, 70 * K, (74, 40, 96, 255), 3))
img.alpha_composite(ridge(horizon, 46 * K, (46, 26, 70, 255), 11))

# riflesso del sole
random.seed(5)
rf = Image.new("RGBA", (w, h), (0, 0, 0, 0))
rd = ImageDraw.Draw(rf)
for i in range(60):
    y = horizon + int(i ** 1.35 * 2.2 * K) + 4 * K
    if y > h:
        break
    half = int(sr * (0.9 - i / 90) * (1 + random.random() * 0.3))
    rd.rectangle([sx - half, y, sx + half, y + int(2.5 * K)], fill=(255, 196, 120, max(0, 190 - i * 3)))
img.alpha_composite(rf.filter(ImageFilter.GaussianBlur(1.2 * K)))

# barca a vela
bx, by = int(w * 0.72), horizon + int(h * 0.035)
bd = ImageDraw.Draw(img)
bd.polygon([(bx - 26 * K, by), (bx + 30 * K, by), (bx + 20 * K, by + 9 * K), (bx - 18 * K, by + 9 * K)], fill=(16, 10, 30, 255))
bd.polygon([(bx, by - 2 * K), (bx, by - 54 * K), (bx + 26 * K, by - 4 * K)], fill=(236, 214, 190, 255))
bd.polygon([(bx - 3 * K, by - 2 * K), (bx - 3 * K, by - 40 * K), (bx - 22 * K, by - 4 * K)], fill=(200, 176, 160, 255))

# PONTE ad archi (sagoma in primo piano)
deck_y, deck_t, n = int(h * 0.80), int(h * 0.028), 7
span = w / n
bridge = Image.new("RGBA", (w, h), (0, 0, 0, 0))
bg = ImageDraw.Draw(bridge)
col = (14, 9, 28, 255)
bg.rectangle([0, deck_y, w, deck_y + deck_t], fill=col)
bg.rectangle([0, deck_y - int(h * 0.012), w, deck_y], fill=col)
for i in range(0, n * 3 + 1):
    px = int(i * span / 3)
    bg.rectangle([px - 4 * K, deck_y - int(h * 0.030), px + 4 * K, deck_y - int(h * 0.010)], fill=col)
body_top, body_bot = deck_y + deck_t, h
bg.rectangle([0, body_top, w, body_bot], fill=col)
arch = Image.new("L", (w, h), 0)
ad = ImageDraw.Draw(arch)
for i in range(n):
    cx, rx, top = int((i + 0.5) * span), int(span * 0.36), body_top + int(h * 0.02)
    ad.rectangle([cx - rx, top + rx, cx + rx, body_bot], fill=255)
    ad.ellipse([cx - rx, top, cx + rx, top + 2 * rx], fill=255)
bridge.putalpha(ImageChops.subtract(bridge.split()[3], arch))
img.alpha_composite(bridge)
under = Image.new("RGBA", (w, h), (0, 0, 0, 0))
ImageDraw.Draw(under).rectangle([0, body_top + int(h * 0.02), w, h], fill=(6, 6, 20, 70))
img.alpha_composite(under)
# lanterne calde sul parapetto
lan = Image.new("RGBA", (w, h), (0, 0, 0, 0))
ld = ImageDraw.Draw(lan)
for i in range(n + 1):
    lx, ly = int(i * span), deck_y - int(h * 0.040)
    ld.ellipse([lx - 14 * K, ly - 14 * K, lx + 14 * K, ly + 14 * K], fill=(255, 190, 90, 150))
img.alpha_composite(lan.filter(ImageFilter.GaussianBlur(9 * K)))
ld2 = ImageDraw.Draw(img)
for i in range(n + 1):
    lx, ly = int(i * span), deck_y - int(h * 0.040)
    ld2.ellipse([lx - 4 * K, ly - 5 * K, lx + 4 * K, ly + 5 * K], fill=(255, 232, 170, 255))


def font(name, size):
    return ImageFont.truetype(os.path.join("C:/Windows/Fonts", name), size)


# titolo
title = "PONTICHEAGE"
tf = font("georgiab.ttf", int(h * 0.15))
td = ImageDraw.Draw(Image.new("L", (w, h), 0))
bbox = td.textbbox((0, 0), title, font=tf)
track = int(h * 0.012)
widths = [td.textlength(c, font=tf) for c in title]
total = sum(widths) + track * (len(title) - 1)
tx, ty = (w - total) / 2, int(h * 0.11)
mask = Image.new("L", (w, h), 0)
md = ImageDraw.Draw(mask)
x = tx
for c, cw in zip(title, widths):
    md.text((x, ty), c, font=tf, fill=255)
    x += cw + track
halo = Image.new("RGBA", (w, h), (255, 170, 70, 0))
halo.putalpha(mask.filter(ImageFilter.GaussianBlur(14 * K)).point(lambda v: int(v * 0.8)))
img.alpha_composite(halo)
sh = Image.new("RGBA", (w, h), (0, 0, 0, 0))
sh.putalpha(mask.filter(ImageFilter.GaussianBlur(3 * K)).point(lambda v: int(v * 0.85)))
img.alpha_composite(sh, (0, int(5 * K)))
ol = Image.new("RGBA", (w, h), (40, 16, 8, 255))
ol.putalpha(mask.filter(ImageFilter.MaxFilter(2 * K + 1)))
img.alpha_composite(ol)
top, bot = ty + bbox[1], ty + bbox[3]
gold = Image.new("RGBA", (w, h))
gp = gold.load()
for yy in range(max(0, top - 4), min(h, bot + 4)):
    t = min(1, max(0, (yy - top) / max(1, bot - top)))
    c = lerp((255, 244, 190), (214, 136, 40), t ** 0.9)
    for xx in range(w):
        gp[xx, yy] = c + (255,)
gold.putalpha(mask)
img.alpha_composite(gold)
shifted = mask.transform(mask.size, Image.AFFINE, (1, 0, 0, 0, 1, int(3 * K)))
hl = Image.new("RGBA", (w, h), (255, 255, 255, 0))
hl.putalpha(ImageChops.subtract(mask, shifted).point(lambda v: min(255, v * 2)))
img.alpha_composite(hl)

# sottotitolo
sf = font("constani.ttf", int(h * 0.04))
sub = "il reame degli amici  -  ponti, mari e avventure"
sm = Image.new("L", (w, h), 0)
sdr = ImageDraw.Draw(sm)
sdr.text(((w - sdr.textlength(sub, font=sf)) / 2, ty + int(h * 0.19)), sub, font=sf, fill=255)
sg = Image.new("RGBA", (w, h), (255, 232, 200, 255))
sg.putalpha(sm.point(lambda v: int(v * 0.92)))
img.alpha_composite(sg)

# vignetta
vg = Image.new("L", (w, h), 0)
ImageDraw.Draw(vg).ellipse([-w * 0.25, -h * 0.35, w * 1.25, h * 1.35], fill=255)
vg = vg.filter(ImageFilter.GaussianBlur(w * 0.12))
dark = Image.new("RGBA", (w, h), (4, 3, 14, 255))
dark.putalpha(vg.point(lambda v: int((255 - v) * 0.75)))
img.alpha_composite(dark)

final = img.convert("RGB").resize((W, H), Image.LANCZOS)
os.makedirs(os.path.dirname(OUT), exist_ok=True)
final.save(OUT, optimize=True)
print("scritto", OUT, os.path.getsize(OUT) // 1024, "KB")
