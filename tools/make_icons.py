"""Erzeugt die Tray-Symbole (ICO) fuer MausAkku im Stil der Vorlage.

Aufruf:  python tools/make_icons.py
Ergebnis in Icons/:
  akku_<theme>_000 ... _100.ico  Akkustand in 5er-Schritten (5 Balken)
  akku_<theme>_laden.ico         Maus haengt am Kabel (Stromstecker)
  akku_<theme>_aus.ico           Maus nicht verbunden (grau, durchgestrichen)
  app.ico                        Programmsymbol
theme: dark  = weisser Rahmen fuer dunkle Taskleiste
       light = schwarzer Rahmen fuer helle Taskleiste
Farben: >= 50 % gruen, 15-49 % orange, < 15 % rot
"""
from pathlib import Path
from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
SS = 8                                  # Supersampling
U = 64                                  # Entwurfsraster 64 x 64

# Verlauf links -> rechts (dunkler -> heller), wie in der Vorlage
GREEN = ((76, 175, 40), (150, 230, 70))
ORANGE = ((232, 80, 40), (247, 150, 30))
RED = ((214, 40, 40), (240, 90, 50))
GREY = (150, 150, 150)
FRAME = {"dark": (255, 255, 255), "light": (20, 20, 20)}
OUT = Path(__file__).resolve().parent.parent / "Icons"

# Geometrie (Einheiten im 64er-Raster)
BODY = (1.5, 13, 55, 51)      # x0, y0, x1, y1 Aussenkante Gehaeuse
RADIUS = 8
STROKE = 5.5
NUB = (56.5, 24, 63, 40)
INNER = (10, 21.5, 46.5, 42.5)  # Bereich fuer Balken/Fuellung


def palette(level):
    if level >= 50:
        return GREEN
    if level >= 15:
        return ORANGE
    return RED


class Canvas:
    def __init__(self, n):
        self.n = n
        self.k = n * SS / U
        self.img = Image.new("RGBA", (n * SS, n * SS), (0, 0, 0, 0))
        self.d = ImageDraw.Draw(self.img)

    def box(self, b):
        return [b[0] * self.k, b[1] * self.k, b[2] * self.k, b[3] * self.k]

    def frame(self, color):
        self.d.rounded_rectangle(self.box(BODY), radius=RADIUS * self.k,
                                 outline=color + (255,), width=round(STROKE * self.k))
        self.d.rounded_rectangle(self.box(NUB), radius=2.5 * self.k, fill=color + (255,))

    def gradient_rect(self, b, cols, radius):
        x0, y0, x1, y1 = [round(v) for v in self.box(b)]
        w, h = max(1, x1 - x0), max(1, y1 - y0)
        grad = Image.new("RGBA", (w, h))
        gd = ImageDraw.Draw(grad)
        (r0, g0, b0), (r1, g1, b1) = cols
        for x in range(w):
            t = x / max(1, w - 1)
            gd.line([(x, 0), (x, h)], fill=(round(r0 + (r1 - r0) * t), round(g0 + (g1 - g0) * t),
                                            round(b0 + (b1 - b0) * t), 255))
        mask = Image.new("L", (w, h), 0)
        ImageDraw.Draw(mask).rounded_rectangle([0, 0, w - 1, h - 1], radius=radius * self.k, fill=255)
        self.img.paste(grad, (x0, y0), mask)

    def result(self):
        return self.img.resize((self.n, self.n), Image.LANCZOS)


def level_icon(n, theme, level):
    c = Canvas(n)
    c.frame(FRAME[theme])
    bars = 0 if level <= 0 else min(5, -(-level // 20))
    cols = palette(level)
    x0, y0, x1, y1 = INNER
    gap = 2.2
    w = (x1 - x0 - 4 * gap) / 5
    for i in range(bars):
        bx = x0 + i * (w + gap)
        c.gradient_rect((bx, y0, bx + w, y1), cols, radius=1.6)
    return c.result()


def charging_icon(n, theme):
    """Rahmen mit gruenem Stromstecker (Kabel links, Stifte nach rechts)."""
    c = Canvas(n)
    c.frame(FRAME[theme])
    g = GREEN[1] + (255,)
    k = c.k
    cy = 32
    # Kabel
    c.d.rounded_rectangle([12 * k, (cy - 2.2) * k, 28 * k, (cy + 2.2) * k], radius=2 * k, fill=g)
    # Steckerkoerper (halbrund nach links)
    c.d.pieslice([22 * k, (cy - 8.5) * k, 39 * k, (cy + 8.5) * k], 270, 90, fill=g)
    c.d.rectangle([30.5 * k, (cy - 8.5) * k, 34 * k, (cy + 8.5) * k], fill=g)
    # zwei Stifte
    for py in (cy - 4.5, cy + 4.5):
        c.d.rounded_rectangle([33 * k, (py - 1.8) * k, 44 * k, (py + 1.8) * k], radius=1.5 * k, fill=g)
    return c.result()


def offline_icon(n, theme):
    """Grauer, durchgestrichener Akku mit Luecke im Rahmen am Strich."""
    c = Canvas(n)
    c.frame(GREY)
    k = c.k
    p0, p1 = (8 * k, 59 * k), (53 * k, 5 * k)
    # Luecke in den Rahmen schneiden
    c.d.line([p0, p1], fill=(0, 0, 0, 0), width=round(14 * k))
    c.d.line([p0, p1], fill=GREY + (255,), width=round(5.5 * k))
    r = 2.75 * k
    for (x, y) in (p0, p1):
        c.d.ellipse([x - r, y - r, x + r, y + r], fill=GREY + (255,))
    return c.result()


def save_ico(path, maker):
    imgs = [maker(n) for n in SIZES]
    imgs[-1].save(path, format="ICO", sizes=[(n, n) for n in SIZES], append_images=imgs[:-1])


def preview(path):
    """Uebersichtsbild aller Symbole (fuer die README)."""
    items = [("100", 100), ("80", 80), ("60", 60), ("45", 45), ("30", 30), ("10", 10), ("laden", None), ("aus", None)]
    cell, pad = 96, 12
    sheet = Image.new("RGBA", (len(items) * (cell + pad) + pad, 2 * (cell + pad) + pad + 40), (255, 255, 255, 255))
    d = ImageDraw.Draw(sheet)
    for row, (theme, bg) in enumerate((("light", (243, 243, 243)), ("dark", (32, 32, 32)))):
        y = pad + row * (cell + pad)
        d.rectangle([0, y - pad // 2, sheet.width, y + cell + pad // 2], fill=bg + (255,))
        for i, (label, lvl) in enumerate(items):
            x = pad + i * (cell + pad)
            if label == "laden":
                im = charging_icon(cell, theme)
            elif label == "aus":
                im = offline_icon(cell, theme)
            else:
                im = level_icon(cell, theme, lvl)
            sheet.alpha_composite(im, (x, y))
            small = (charging_icon(16, theme) if label == "laden" else offline_icon(16, theme)
                     if label == "aus" else level_icon(16, theme, lvl))
            sheet.alpha_composite(small.resize((32, 32), Image.NEAREST), (x + cell - 32, y + cell - 32))
    for i, (label, _) in enumerate(items):
        d.text((pad + i * (cell + pad) + 30, 2 * (cell + pad) + pad + 8),
               label + (" %" if label.isdigit() else ""), fill=(0, 0, 0, 255))
    sheet.save(path)


def main():
    OUT.mkdir(exist_ok=True)
    for theme in ("dark", "light"):
        for level in range(0, 101, 5):
            save_ico(OUT / f"akku_{theme}_{level:03d}.ico", lambda n, l=level, th=theme: level_icon(n, th, l))
        save_ico(OUT / f"akku_{theme}_laden.ico", lambda n, th=theme: charging_icon(n, th))
        save_ico(OUT / f"akku_{theme}_aus.ico", lambda n, th=theme: offline_icon(n, th))
    save_ico(OUT / "app.ico", lambda n: level_icon(n, "light", 100))
    preview(OUT.parent / "symbole-vorschau.png")


if __name__ == "__main__":
    main()
