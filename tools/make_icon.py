"""StreamMirror VR icon (Reflection tile, dot on the mirror line, cyan-leaning blue): drawings plus the final PNGs and .ico.
Run it to rebuild dist/logo/final. make_compare.py borrows the drawing functions for study sheets."""
import math, os, subprocess
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
EDGE = r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
BLUE, BLUE2, DOT = '#2E9BFF', '#1E6FE0', 'line'
ICE, RED = '#C9D6FF', '#FF3B4D'
TILE_TOP, TILE_BOTTOM = '#17213F', '#10162C'


def squircle(x, y, size, n=5.0, steps=160):
    """A continuous-corner tile (superellipse), the shape Windows 11 and macOS tiles use."""
    a = size / 2
    pts = []
    for i in range(steps):
        t = 2 * math.pi * i / steps
        c, s = math.cos(t), math.sin(t)
        pts.append((x + a + a * math.copysign(abs(c) ** (2 / n), c), y + a + a * math.copysign(abs(s) ** (2 / n), s)))
    return 'M' + 'L'.join(f'{px:.2f} {py:.2f}' for px, py in pts) + 'Z'


def master(blue, blue2, dot):
    """256 px drawing: tapered visor with strap stubs, mirror line, shortening reflection lines."""
    strap = blue2
    s = ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256"><defs>'
         f'<linearGradient id="t" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{TILE_TOP}"/><stop offset="1" stop-color="{TILE_BOTTOM}"/></linearGradient>'
         f'<linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{blue}"/><stop offset="1" stop-color="{blue2}"/></linearGradient></defs>'
         f'<path d="{squircle(16, 16, 224)}" fill="url(#t)"/>'
         f'<path d="{squircle(17, 17, 222)}" fill="none" stroke="#FFFFFF" stroke-opacity="0.09" stroke-width="2"/>'
         '<g transform="translate(0 -3)">'
         f'<rect x="46" y="80" width="18" height="28" rx="8" fill="{strap}"/><rect x="192" y="80" width="18" height="28" rx="8" fill="{strap}"/>'
         '<path d="M80 60H176Q196 60 196 80L192 104Q190 126 168 126H150Q144 126 140 116Q134 102 128 102Q122 102 116 116'
         'Q112 126 106 126H88Q66 126 64 104L60 80Q60 60 80 60Z" fill="url(#g)"/>')
    if dot == 'line':
        s += (f'<rect x="52" y="139" width="58" height="4" rx="2" fill="{ICE}"/><rect x="146" y="139" width="58" height="4" rx="2" fill="{ICE}"/>'
              f'<circle cx="128" cy="141" r="11" fill="{RED}"/>')
    else:
        s += (f'<rect x="52" y="139" width="152" height="4" rx="2" fill="{ICE}"/>'
              f'<circle cx="166" cy="86" r="15" fill="{TILE_BOTTOM}"/><circle cx="166" cy="86" r="9" fill="{RED}"/>')
    for y, w, h, op in ((153, 136, 9, .55), (167, 108, 7, .40), (179, 80, 6, .28), (190, 52, 5, .18)):
        s += f'<rect x="{128 - w / 2}" y="{y}" width="{w}" height="{h}" rx="{h / 2}" fill="{blue}" opacity="{op}"/>'
    return s + '</g></svg>'


def small(n, blue, blue2, dot):
    """Hand-placed drawings on the pixel grid for 32, 24 and 16 px."""
    k = {32: dict(r=7, stub=(3, 9, 3, 5), top=(5, 27, 6), bot=(6, 26, 16), notch=(3, 3), line=(4, 19, 24, 1),
                  dot_line=(16, 19.5, 2.4), lens=(21, 10, 2.6, 1.6), bars=[(7, 22, 18, 2, .55), (10, 25, 12, 1, .4), (12, 27, 8, 1, .25)]),
             24: dict(r=5, stub=(2, 7, 2, 3), top=(4, 20, 4), bot=(5, 19, 12), notch=(2, 2), line=(3, 14, 18, 1),
                      dot_line=(12, 14.5, 1.9), lens=(15.5, 7.5, 2, 1.25), bars=[(5, 16, 14, 1, .55), (8, 18, 8, 1, .35)]),
             16: dict(r=3, stub=(1, 4, 1, 3), top=(2, 14, 3), bot=(3, 13, 8), notch=(1.5, 1.5), line=(2, 10, 12, 1),
                      dot_line=(8, 10.5, 1.5), lens=(10.5, 5, 1.5, 1), bars=[(4, 12, 8, 1, .5)])}[n]
    (tx0, tx1, ty), (bx0, bx1, by), (nw, nh) = k['top'], k['bot'], k['notch']
    c, r = n / 2, 2 if n > 16 else 1
    body = (f'M{tx0 + r} {ty}H{tx1 - r}Q{tx1} {ty} {tx1} {ty + r}L{bx1} {by - r}Q{bx1} {by} {bx1 - r} {by}H{c + nw}'
            f'L{c + nw * 0.4} {by - nh}H{c - nw * 0.4}L{c - nw} {by}H{bx0 + r}Q{bx0} {by} {bx0} {by - r}L{tx0} {ty + r}Q{tx0} {ty} {tx0 + r} {ty}Z')
    sx, sy, sw, sh = k['stub']
    s = (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {n} {n}" width="{n}" height="{n}">'
         f'<defs><linearGradient id="t" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{TILE_TOP}"/><stop offset="1" stop-color="{TILE_BOTTOM}"/></linearGradient></defs>'
         f'<rect width="{n}" height="{n}" rx="{k["r"]}" fill="url(#t)"/>'
         f'<rect x="{sx}" y="{sy}" width="{sw}" height="{sh}" fill="{blue2}"/><rect x="{n - sx - sw}" y="{sy}" width="{sw}" height="{sh}" fill="{blue2}"/>'
         f'<path d="{body}" fill="{blue}"/>')
    lx, ly, lw, lh = k['line']
    if dot == 'line':
        dx, dy, dr = k['dot_line']
        gap = dr + (1 if n > 16 else 0.5)
        s += (f'<rect x="{lx}" y="{ly}" width="{dx - gap - lx}" height="{lh}" fill="{ICE}"/>'
              f'<rect x="{dx + gap}" y="{ly}" width="{lx + lw - dx - gap}" height="{lh}" fill="{ICE}"/>'
              f'<circle cx="{dx}" cy="{dy}" r="{dr}" fill="{RED}"/>')
    else:
        ex, ey, er, ir = k['lens']
        s += (f'<rect x="{lx}" y="{ly}" width="{lw}" height="{lh}" fill="{ICE}"/>'
              f'<circle cx="{ex}" cy="{ey}" r="{er}" fill="{TILE_BOTTOM}"/><circle cx="{ex}" cy="{ey}" r="{ir}" fill="{RED}"/>')
    for bx, by_, bw, bh, op in k['bars']:
        s += f'<rect x="{bx}" y="{by_}" width="{bw}" height="{bh}" fill="{blue}" opacity="{op}"/>'
    return s + '</svg>'




def build(out=os.path.join(os.path.dirname(HERE), 'dist', 'logo', 'final')):
    os.makedirs(out, exist_ok=True)
    for old in os.listdir(out):
        if old.startswith('icon'):
            os.remove(os.path.join(out, old))
    drawings = {256: master(BLUE, BLUE2, DOT), 32: small(32, BLUE, BLUE2, DOT), 24: small(24, BLUE, BLUE2, DOT), 16: small(16, BLUE, BLUE2, DOT)}
    for n, text in drawings.items():
        open(os.path.join(out, f'icon-{n}.svg'), 'w', encoding='utf-8').write(text)
    # Each size from the drawing meant for it: 16/20 from 16, 24 from 24, 32/40 from 32, the rest from the master.
    sizes = {16: 16, 20: 16, 24: 24, 32: 32, 40: 32, 48: 256, 64: 256, 96: 256, 128: 256, 256: 256}
    x, cells, places = 0, '', {}
    for size, src in sizes.items():
        cells += f'<img src="icon-{src}.svg" width="{size}" height="{size}" style="position:absolute;left:{x}px;top:0">'
        places[size] = x
        x += size + 8
    page, shot = os.path.join(out, '_render.html'), os.path.join(out, '_render.png')
    open(page, 'w', encoding='utf-8').write(f'<!doctype html><body style="margin:0;background:transparent">{cells}</body>')
    subprocess.run([EDGE, '--headless=new', '--disable-gpu', '--hide-scrollbars', '--allow-file-access-from-files',
                    '--default-background-color=00000000', f'--window-size={x + 100},{256 + 240}', f'--screenshot={shot}',
                    'file:///' + page.replace('\\', '/')], check=True, capture_output=True)
    strip = Image.open(shot).convert('RGBA')
    images = [strip.crop((left, 0, left + size, size)) for size, left in places.items()]
    for size, im in zip(sizes, images):
        im.save(os.path.join(out, f'icon-{size}.png'))
    images[-1].save(os.path.join(out, 'icon.ico'), format='ICO', sizes=[(s, s) for s in sizes], append_images=images[:-1])
    os.remove(page); os.remove(shot)
    return sorted(Image.open(os.path.join(out, 'icon.ico')).info['sizes'])


if __name__ == '__main__':
    print('icon.ico sizes:', build())
