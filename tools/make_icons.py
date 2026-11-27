"""Erzeugt die Tray-Symbole (ICO) fuer MausAkku – hochkant stehende Maus,
die sich je nach Akkustand von unten nach oben fuellt.

Jede Groesse wird eigens und pixelgenau gezeichnet (Kanten auf ganzen Pixeln,
Linienstaerke je Groesse angepasst, weniger Details bei kleinen Groessen),
damit das Symbol im Infobereich bei jeder Windows-Skalierung scharf ist:
  100 % -> 16 px, 125 % -> 20 px, 150 % -> 24 px, 175 % -> 28 px,
  200 % -> 32 px, 225 % -> 36 px, 250 % -> 40 px, 300 % -> 48 px

Aufruf:  python tools/make_icons.py
Ergebnis in Icons/:
  akku_<theme>_000 ... _100.ico  Akkustand in 5er-Schritten
  akku_<theme>_laden.ico         Maus haengt am Kabel (gruener Stecker)
  akku_<theme>_aus.ico           Maus nicht verbunden (grau, durchgestrichen)
  app.ico                        Programmsymbol
theme: dark  = weisse Linien fuer dunkle Taskleiste
       light = schwarze Linien fuer helle Taskleiste
Farben: >= 50 % gruen, 15-49 % gelb, < 15 % rot
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageChops

SIZES = [16, 20, 24, 28, 32, 36, 40, 48, 64, 256]
SS = 8  # Supersampling (Kanten liegen trotzdem auf ganzen Pixeln)

GREEN = ((50, 165, 35), (110, 215, 55))
YELLOW = ((235, 180, 0), (255, 220, 50))
RED = ((205, 30, 30), (245, 80, 55))
PLUG = (90, 205, 50)
GREY = (150, 150, 150)
LINE = {"dark": (255, 255, 255), "light": (15, 15, 15)}
OUT = Path(__file__).resolve().parent.parent / "Icons"


def palette(level):
    if level >= 50:
        return GREEN
    if level >= 15:
        return YELLOW
    return RED


class Mouse:
    """Pixelgenaue Geometrie fuer eine Symbolgroesse n."""

    def __init__(self, n):
        self.n = n
        # Linienstaerke in ganzen Pixeln
        self.s = {16: 2, 20: 2, 24: 2, 28: 3, 32: 3, 36: 3, 40: 4, 48: 4, 64: 5}.get(n, round(n / 12))
        self.center_w = max(1, round(self.s * 0.6))
        # Maus moeglichst breit, damit die Fuellung gut sichtbar ist
        x0 = round(n * 0.12)
        w = n - 2 * x0
        if (w - self.center_w) % 2:      # Mittellinie exakt auf Pixel setzen
            w -= 1
        self.x0, self.x1 = x0, x0 + w    # x1 exklusiv
        self.y0, self.y1 = 0, n
        self.radius = w / 2
        self.cx0 = self.x0 + (w - self.center_w) // 2   # Mittellinie (links)
        self.divide = round(n * 0.44)                    # Trennlinie Tasten/Handballen
        self.small = n < 24
        # Innenbereich
        self.ix0, self.ix1 = self.x0 + self.s, self.x1 - self.s
        self.iy0, self.iy1 = self.y0 + self.s, self.y1 - self.s


class Canvas:
    def __init__(self, n):
        self.n = n
        self.m = Mouse(n)
        self.img = Image.new("RGBA", (n * SS, n * SS), (0, 0, 0, 0))
        self.d = ImageDraw.Draw(self.img)

    @staticmethod
    def p(*v):
        return [x * SS for x in v]

    def inner_mask(self):
        m = self.m
        mask = Image.new("L", self.img.size, 0)
        ImageDraw.Draw(mask).rounded_rectangle(
            self.p(m.ix0, m.iy0, m.ix1, m.iy1), radius=max(1, (m.radius - m.s)) * SS, fill=255)
        return mask

    def fill_level(self, level, cols):
        if level <= 0:
            return
        m = self.m
        inner_h = m.iy1 - m.iy0
        top = m.iy1 - max(1, round(inner_h * level / 100))   # auf ganze Pixel gerundet
        w, h = self.img.size
        grad = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        gd = ImageDraw.Draw(grad)
        (r0, g0, b0), (r1, g1, b1) = cols
        y_top, y_bot = top * SS, m.iy1 * SS
        for y in range(y_top, y_bot):
            t = (y_bot - y) / max(1, inner_h * SS)
            gd.line([(0, y), (w, y)], fill=(round(r0 + (r1 - r0) * t), round(g0 + (g1 - g0) * t),
                                            round(b0 + (b1 - b0) * t), 255))
        cut = Image.new("L", (w, h), 0)
        ImageDraw.Draw(cut).rectangle([0, y_top, w, h], fill=255)
        fmask = ImageChops.multiply(self.inner_mask(), cut)
        self.img.paste(grad, (0, 0), fmask)
        layer = Image.new("RGBA", self.img.size, (0, 0, 0, 0))
        layer.paste(grad, (0, 0), fmask)
        self._fill_layer = layer

    def outline(self, color):
        m = self.m
        c = color + (255,)
        # Ring = aussen gefuellt minus innen
        outer = Image.new("L", self.img.size, 0)
        ImageDraw.Draw(outer).rounded_rectangle(self.p(m.x0, m.y0, m.x1, m.y1), radius=m.radius * SS, fill=255)
        ring = ImageChops.subtract(outer, self.inner_mask())
        self.img.paste(Image.new("RGBA", self.img.size, c), (0, 0), ring)

    def details(self, color):
        m = self.m
        c = color + (255,)
        # Mittellinie von oben bis zur Trennlinie
        self.d.rectangle(self.p(m.cx0, m.iy0, m.cx0 + m.center_w, m.divide), fill=c)
        # Trennlinie quer
        tw = m.center_w
        if m.small:
            self.d.rectangle(self.p(m.ix0, m.divide, m.ix1, m.divide + tw), fill=c)
        else:
            pts = []
            for i in range(21):
                t = i / 20
                x = m.ix0 + (m.ix1 - m.ix0) * t
                y = m.divide + tw / 2 + 0.8 * tw * (1 - (2 * t - 1) ** 2) - 0.4 * tw
                pts.append((x * SS, y * SS))
            self.d.line(pts, fill=c, width=round(tw * SS), joint="curve")
            if m.n >= 28:
                # Mausrad als Ring (wie in der Vorlage)
                ring_w = max(1, round(m.s * 0.7))
                ww = m.center_w + 2 * (ring_w + 1)
                wh = round(m.n * 0.22)
                wx0 = m.cx0 + m.center_w / 2 - ww / 2
                wy0 = round(m.n * 0.14)
                outer = Image.new("L", self.img.size, 0)
                ImageDraw.Draw(outer).rounded_rectangle(self.p(wx0, wy0, wx0 + ww, wy0 + wh), radius=ww / 2 * SS, fill=255)
                inner = Image.new("L", self.img.size, 0)
                ImageDraw.Draw(inner).rounded_rectangle(
                    self.p(wx0 + ring_w, wy0 + ring_w, wx0 + ww - ring_w, wy0 + wh - ring_w),
                    radius=(ww / 2 - ring_w) * SS, fill=255)
                # Innen das Rad freistellen, damit die Mittellinie dort nicht durchlaeuft
                self.img.paste(Image.new("RGBA", self.img.size, (0, 0, 0, 0)), (0, 0), inner)
                self._refill(inner)
                self.img.paste(Image.new("RGBA", self.img.size, c), (0, 0), ImageChops.subtract(outer, inner))

    def _refill(self, mask):
        """Stellt unter dem Rad-Innenraum die Fuellung wieder her."""
        if getattr(self, "_fill_layer", None) is not None:
            self.img.paste(self._fill_layer, (0, 0), ImageChops.multiply(mask, self._fill_layer.getchannel("A")))

    def result(self):
        return self.img.resize((self.n, self.n), Image.LANCZOS)


def level_icon(n, theme, level):
    c = Canvas(n)
    line = RED[0] if level == 0 else LINE[theme]
    c.fill_level(level, palette(level))
    c.outline(line)
    c.details(line)
    return c.result()


def charging_icon(n, theme):
    """Maus mit gruenem Stecker im Handballen-Bereich (Stifte oben, Kabel unten)."""
    c = Canvas(n)
    c.outline(LINE[theme])
    m = c.m
    g = PLUG + (255,)
    cx = (m.ix0 + m.ix1) / 2
    iw = m.ix1 - m.ix0
    top, bot = m.iy0 + 1, m.iy1          # ganzer Innenraum fuer gute Erkennbarkeit
    h = bot - top
    body_w = iw * 0.86
    pin_w = max(1, round(iw * 0.18))
    pin_gap = iw * 0.24
    y_pins = top + h * 0.12
    y_body = top + h * 0.36
    y_body_end = top + h * 0.72
    for px in (cx - pin_gap - pin_w / 2, cx + pin_gap - pin_w / 2):
        c.d.rectangle(c.p(round(px), y_pins, round(px) + pin_w, y_body + 1), fill=g)
    c.d.rounded_rectangle(c.p(cx - body_w / 2, y_body, cx + body_w / 2, y_body_end),
                          radius=body_w * 0.3 * SS, fill=g)
    cw = max(1, round(iw * 0.22))
    c.d.rectangle(c.p(cx - cw / 2, y_body_end - 1, cx + cw / 2, bot), fill=g)
    return c.result()


def offline_icon(n, theme):
    """Graue Maus mit Mittellinie, durchgestrichen."""
    c = Canvas(n)
    c.outline(GREY)
    c.details(GREY)
    s = c.m.s
    p0, p1 = (0.5 * SS, (n - 0.5) * SS), ((n - 0.5) * SS, 0.5 * SS)
    c.d.line([p0, p1], fill=(0, 0, 0, 0), width=round(s * 2.2 * SS))   # Luecke
    c.d.line([p0, p1], fill=GREY + (255,), width=round(s * 0.9 * SS))
    return c.result()


def save_ico(path, maker):
    imgs = [maker(n) for n in SIZES]
    imgs[-1].save(path, format="ICO", sizes=[(n, n) for n in SIZES], append_images=imgs[:-1])


def preview(path):
    """Vorschau: so gross wie in der Taskleiste (16/20/24/32 px), 4-fach vergroessert."""
    items = [("100", 100), ("75", 75), ("50", 50), ("40", 40), ("20", 20), ("10", 10), ("laden", None), ("aus", None)]
    sizes = [16, 20, 24, 32]
    zoom = 4
    colw = 32 * zoom + 16
    rowh = 32 * zoom + 16
    W = 90 + len(items) * colw
    H = 30 + 2 * len(sizes) * rowh
    sheet = Image.new("RGBA", (W, H), (255, 255, 255, 255))
    d = ImageDraw.Draw(sheet)

    def make(label, lvl, n, theme):
        if label == "laden":
            return charging_icon(n, theme)
        if label == "aus":
            return offline_icon(n, theme)
        return level_icon(n, theme, lvl)

    for i, (label, _) in enumerate(items):
        d.text((90 + i * colw + 50, 8), label + (" %" if label.isdigit() else ""), fill=(0, 0, 0, 255))
    r = 0
    for theme, bg in (("light", (243, 243, 243)), ("dark", (32, 32, 32))):
        for n in sizes:
            y = 30 + r * rowh
            d.rectangle([0, y, W, y + rowh], fill=bg + (255,))
            d.text((8, y + rowh // 2 - 6), f"{n} px", fill=(128, 128, 128, 255))
            for i, (label, lvl) in enumerate(items):
                im = make(label, lvl, n, theme).resize((n * zoom, n * zoom), Image.NEAREST)
                ox = 90 + i * colw + (colw - n * zoom) // 2
                oy = y + (rowh - n * zoom) // 2
                sheet.alpha_composite(im, (ox, oy))
            r += 1
    sheet.save(path)


def main():
    OUT.mkdir(exist_ok=True)
    for theme in ("dark", "light"):
        for level in range(0, 101, 5):
            save_ico(OUT / f"akku_{theme}_{level:03d}.ico", lambda n, l=level, th=theme: level_icon(n, th, l))
        save_ico(OUT / f"akku_{theme}_laden.ico", lambda n, th=theme: charging_icon(n, th))
        save_ico(OUT / f"akku_{theme}_aus.ico", lambda n, th=theme: offline_icon(n, th))
    save_ico(OUT / "app.ico", lambda n: level_icon(n, "light", 75))
    preview(OUT.parent / "symbole-vorschau.png")


if __name__ == "__main__":
    main()
