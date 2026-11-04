"""Plan the widened Myeongji map: new roads, zone polygons, moved shops and doors, distance report, review picture.

Writes layout_expanded.json, zones.json, anchors_expanded.json and expansion_report.json next to the original data,
which it only reads. Then run: build_myeongji_routes.py --expanded
--picture PATH also renders the review picture. Every coordinate is world x, z in metres.
"""
import argparse, copy, json, math, random, sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
import build_myeongji_routes as routes

BOUNDS_MIN, BOUNDS_SIZE = (-90.0, -150.0), (290.0, 235.0)
OPEN_ORDER = ['C', 'A', 'B', 'D']
# Zone borders stay on the far kerb side of a border road, so that road belongs to the zone that opens first.
ZONES = {
    'A': [(-90, 85), (31, 85), (31, -47.5), (-90, -47.5)],
    'B': [(31, 85), (200, 85), (200, -6), (118.8, -6), (93.2, -47.5), (31, -47.5)],
    'C': [(-90, -47.5), (93.2, -47.5), (89.9, -52), (69.9, -103), (69.9, -150), (-90, -150)],
    'D': [(93.2, -47.5), (118.8, -6), (200, -6), (200, -150), (69.9, -150), (69.9, -103), (89.9, -52)],
}
LABELS = {'A': (-30, 62), 'B': (98, 62), 'C': (-24, -122), 'D': (138, -72)}
NEW_ROADS = [
    # A: north and west of the old block.
    ('a_main_w1', (-10, 0), (-46, 0), 8), ('a_main_w2', (-46, 0), (-84, -6), 8),
    ('a_west_n1', (0, 0), (0, 38), 6), ('a_west_n2', (0, 38), (-8, 78), 5),
    ('a_conn_n', (27, 0), (27, 78), 5),
    ('a_cross1', (27, 38), (0, 38), 7), ('a_cross2', (0, 38), (-84, 30), 7),
    ('a_top', (-84, 78), (27, 78), 6),
    ('a_mid_n', (-44, 78), (-44, 33.81), 5), ('a_mid_s', (-44, 33.81), (-44, 0), 5),
    ('a_south', (-30, 0), (-30, -52), 5),
    ('a_outer', (-84, 78), (-84, -52), 6),
    ('a_alley1', (-64, 31.9), (-64, 54), 4), ('a_alley2', (-64, 54), (-44, 54), 3),
    ('a_lane', (-30, -24), (-14, -24), 3),
    # C: west and south.
    ('c_back_w', (0, -52), (-84, -52), 7),
    ('c_outer1', (-84, -52), (-84, -104), 6), ('c_outer2', (-84, -104), (-78, -141), 6),
    ('c_south_w1', (0, -103), (-46, -100), 6), ('c_south_w2', (-46, -100), (-84, -104), 6),
    ('c_mid1', (-46, -52), (-46, -100), 5), ('c_mid2', (-46, -100), (-40, -143), 5),
    ('c_s1', (-78, -141), (-40, -143), 6), ('c_s2', (-40, -143), (0, -141), 6), ('c_s3', (0, -141), (64, -143), 6),
    ('c_x0', (0, -103), (0, -141), 6), ('c_x64', (64, -103), (64, -143), 6),
    ('c_alley1', (22, -141.69), (22, -118), 3), ('c_alley2', (22, -118), (44, -118), 3),
    ('c_alley3', (-64, -52), (-64, -78), 3),
    # B: north band above the main street, east to the map edge.
    ('b_mid1', (74, 0), (74, 48), 5), ('b_mid2', (74, 48), (78, 80), 5),
    ('b_east1', (116, 0), (122, 44), 6), ('b_east2', (122, 44), (122, 80), 6),
    ('b_cross1', (27, 50), (74, 48), 7), ('b_cross2', (74, 48), (122, 44), 7), ('b_cross3', (122, 44), (192, 48), 7),
    ('b_top1', (27, 78), (78, 80), 6), ('b_top2', (78, 80), (192, 80), 6),
    ('b_main_e', (124, 0), (192, 0), 8),
    ('b_link', (143, 45.2), (143, 0), 5),
    ('b_outer', (192, 80), (192, 0), 6),
    ('b_alley1', (50, 49.02), (50, 18), 4), ('b_alley2', (50, 18), (63, 18), 3),
    ('b_alley3', (168, 46.63), (168, 20), 3),
    # D: east and south.
    ('d_outer', (192, 0), (192, -141), 6),
    ('d_top1', (117, -44), (155, -40), 5), ('d_top2', (155, -40), (192, -44), 5),
    ('d_bot1', (117, -103), (155, -100), 6), ('d_bot2', (155, -100), (192, -103), 6),
    ('d_mid1', (155, 0), (155, -40), 5), ('d_mid2', (155, -40), (155, -100), 5), ('d_mid3', (155, -100), (158, -141), 5),
    ('d_x117', (117, -103), (117, -141), 6),
    ('d_s1', (64, -143), (117, -141), 6), ('d_s2', (117, -141), (192, -141), 6),
    ('d_alley1', (174, -42.05), (174, -72), 3), ('d_alley2', (174, -72), (155, -72), 3),
    ('d_alley3', (136, -141), (136, -118), 3),
]
SIDEWALK = 2.0
CANDIDATE_STEP, DOOR_OFFSET = 8.0, 1.5
BUILDING_ROOM = 8.0  # plot depth a future building needs behind a new anchor
BORDER_CLEARANCE = 6.0
MIN_KEPT_PER_ZONE = 4
MIN_KEPT_SHOPS = 1
HOME_SHOP_REACH = 60.0  # the first pickup of day 1 stays close to home
NEW_SHOP_ROAD_WIDTH = 5  # new shops stand on real streets, not narrow alleys
NAMED_SHOPS = {'A_Shop_01', 'A_Shop_02', 'B_Shop_03', 'B_Shop_04', 'C_Shop_05'}
THRESHOLDS = (120, 220, 300)
SEARCH_STEPS, SEED = 60000, 14102026


def zone_at(x, z):
    return next((k for k, p in ZONES.items() if routes.inside(p, x, z)), None)


def polygon_area(p):
    return abs(sum(p[i][0] * p[(i + 1) % len(p)][1] - p[(i + 1) % len(p)][0] * p[i][1] for i in range(len(p)))) / 2


def segment_cross(a, b, c, d):
    u, v = (b[0] - a[0], b[1] - a[1]), (d[0] - c[0], d[1] - c[1])
    den = u[0] * v[1] - u[1] * v[0]
    if abs(den) < 1e-9:
        return None
    w = (c[0] - a[0], c[1] - a[1])
    t, q = (w[0] * v[1] - w[1] * v[0]) / den, (w[0] * u[1] - w[1] * u[0]) / den
    return (a[0] + t * u[0], a[1] + t * u[1]) if 0 <= t <= 1 and 0 <= q <= 1 else None


def segment_distance(p, a, b):
    return routes.dist(p, routes.project(p, {'a': a, 'b': b}))


def border_edges():
    """Edges two zones share: (zone, zone, point, point)."""
    edges = []
    ids = list(ZONES)
    for i, m in enumerate(ids):
        pm = ZONES[m]
        for n in ids[i + 1:]:
            pn = ZONES[n]
            for k in range(len(pm)):
                a, b = pm[k], pm[(k + 1) % len(pm)]
                for l in range(len(pn)):
                    c, d = pn[l], pn[(l + 1) % len(pn)]
                    # Collinear overlap, clipped to the part both edges cover.
                    ux, uz = b[0] - a[0], b[1] - a[1]
                    length = math.hypot(ux, uz)
                    if abs(ux * (c[1] - a[1]) - uz * (c[0] - a[0])) / length > .01 or abs(ux * (d[1] - a[1]) - uz * (d[0] - a[0])) / length > .01:
                        continue
                    tc, td = sorted(((c[0] - a[0]) * ux + (c[1] - a[1]) * uz) / length ** 2 for c in (c, d))
                    lo, hi = max(0, tc), min(1, td)
                    if hi - lo < 1e-4:
                        continue
                    edges.append((m, n, (a[0] + ux * lo, a[1] + uz * lo), (a[0] + ux * hi, a[1] + uz * hi)))
    return edges


def barriers(roads):
    """Every place a road crosses a shared zone border: a barricade spot."""
    spots = []
    for m, n, p, q in border_edges():
        for r in roads:
            hit = segment_cross(tuple(r['a']), tuple(r['b']), p, q)
            if hit is None:
                continue
            dx, dz = r['b'][0] - r['a'][0], r['b'][1] - r['a'][1]
            length = math.hypot(dx, dz)
            if any(routes.dist(hit, s['position']) < .1 and s['road'] == r['id'] for s in spots):
                continue  # a road crossing exactly where two border edges meet
            spots.append({'id': f'{m}{n}_{r["id"]}', 'zones': [m, n], 'requiredZones': [m, n], 'road': r['id'],
                          'position': [round(hit[0], 2), round(hit[1], 2)], 'roadDirection': [round(dx / length, 4), round(dz / length, 4)],
                          'width': r['width'], 'span': r['width'] + 2 * SIDEWALK})
    return spots


def check_lots_clear(roads_new, lots):
    bad = []
    for r in roads_new:
        reach = r['width'] / 2 + SIDEWALK
        for lot in lots:
            ident, _, x, z, w, d, _, angle = lot
            if angle % 180:
                w, d = d, w
            for t in [i / 40 for i in range(41)]:
                px, pz = r['a'][0] + (r['b'][0] - r['a'][0]) * t, r['a'][1] + (r['b'][1] - r['a'][1]) * t
                if abs(px - x) < w / 2 + reach and abs(pz - z) < d / 2 + reach:
                    bad.append((r['id'], ident))
                    break
    return bad


def junction_spacing(roads):
    """Closest pair of distinct junctions on roads wide enough for cars, which need room to turn."""
    traffic = [r for r in roads if r['width'] >= 6]
    points = []
    for i, r in enumerate(traffic):
        for s in traffic[i + 1:]:
            hit = segment_cross(tuple(r['a']), tuple(r['b']), tuple(s['a']), tuple(s['b']))
            if hit:
                points.append(hit)
        for p in (r['a'], r['b']):
            if any(s is not r and segment_distance(p, tuple(s['a']), tuple(s['b'])) < s['width'] / 2 + .05 for s in traffic):
                points.append(tuple(p))
    unique = []
    for p in points:
        if all(routes.dist(p, q) > 3 for q in unique):  # the car baker merges junctions closer than 3 m
            unique.append(p)
    return min((routes.dist(a, b), a, b) for i, a in enumerate(unique) for b in unique[i + 1:]), len(unique)


def new_candidates(roads_new, roads_all):
    out = []
    for r in roads_new:
        a, b = r['a'], r['b']
        length = routes.dist(a, b)
        ux, uz = (b[0] - a[0]) / length, (b[1] - a[1]) / length
        nx, nz = -uz, ux
        offset = r['width'] / 2 + DOOR_OFFSET
        for step in range(1, int(length // CANDIDATE_STEP) + 1):
            along = step * CANDIDATE_STEP
            if along > length - 4:
                break
            q = (a[0] + ux * along, a[1] + uz * along)
            for side in (-1, 1):
                p = (q[0] + nx * side * offset, q[1] + nz * side * offset)
                behind = (p[0] + nx * side * (SIDEWALK + BUILDING_ROOM), p[1] + nz * side * (SIDEWALK + BUILDING_ROOM))
                zone = zone_at(*p)
                if zone is None or zone_at(*behind) != zone:
                    continue
                if any(s is not r and (segment_distance(p, tuple(s['a']), tuple(s['b'])) < s['width'] / 2 + 5
                                       or segment_distance(behind, tuple(s['a']), tuple(s['b'])) < s['width'] / 2 + 1) for s in roads_all):
                    continue
                if any(segment_distance(p, m, n) < BORDER_CLEARANCE for _, _, m, n in border_edges()):
                    continue
                out.append({'type': 'new', 'zone': zone, 'building': None, 'road': r['id'],
                            'position': [round(p[0], 2), 0.0, round(p[1], 2)], 'forward': [round(-nx * side, 4), 0.0, round(-nz * side, 4)],
                            'roadPoint': [round(q[0], 2), -0.14, round(q[1], 2)]})
    return out


def search(zone_pool, shops_old, doors_old, cost, samples, home_cost, road_width):
    """Annealing over which candidate serves each shop and door slot of a zone."""
    rng = random.Random(SEED)
    pool = {z: [i for i, c in enumerate(zone_pool) if c['zone'] == z] for z in OPEN_ORDER}
    shop_pool = {z: [i for i in pool[z] if zone_pool[i]['type'] == 'shop' or (zone_pool[i]['type'] == 'new' and road_width[zone_pool[i]['road']] >= NEW_SHOP_ROAD_WIDTH)] for z in pool}
    door_pool = {z: [i for i in pool[z] if zone_pool[i]['type'] in ('new', 'door')] for z in pool}
    state = {}
    for z in OPEN_ORDER:
        state[z] = {'shop': [zone_pool.index(a) for a in shops_old[z]], 'door': [zone_pool.index(a) for a in doors_old[z]]}

    def score(s):
        total = {t: [0, 0] for t in ('c120', 'ca220', 'x300', 'same')}
        penalty = 0.0
        for z in OPEN_ORDER:
            chosen = s[z]['shop'] + s[z]['door']
            kept = sum(zone_pool[i]['type'] != 'new' for i in chosen)
            penalty += max(0, MIN_KEPT_PER_ZONE - kept) * 2
            kept_shops = [i for i in s[z]['shop'] if zone_pool[i]['type'] != 'new']
            penalty += max(0, MIN_KEPT_SHOPS - len(kept_shops)) * 2
            penalty -= .05 * sum(zone_pool[i].get('name') in NAMED_SHOPS for i in kept_shops)
            if z == 'C':
                penalty += max(0, min(home_cost[i] for i in s[z]['shop']) - HOME_SHOP_REACH) / 10
            if len(set(chosen)) != len(chosen):
                penalty += 5
            for a, b in zip(s[z]['shop'], s[z]['shop'][1:] + s[z]['shop'][:1]):
                penalty += max(0, 70 - cost[a][b]) / 25
            for i, a in enumerate(s[z]['door']):
                for b in s[z]['door'][i + 1:]:
                    penalty += max(0, 35 - cost[a][b]) / 25
                for b in s[z]['shop']:
                    penalty += max(0, 25 - cost[a][b]) / 25
            # Slots must cover the zone: distance from any road point to its nearest door and shop.
            door_reach = sum(min(routes.dist(p, (zone_pool[d]['position'][0], zone_pool[d]['position'][2])) for d in s[z]['door']) for p in samples[z]) / len(samples[z])
            shop_reach = sum(min(routes.dist(p, (zone_pool[d]['position'][0], zone_pool[d]['position'][2])) for d in s[z]['shop']) for p in samples[z]) / len(samples[z])
            penalty += door_reach / 40 + shop_reach / 80
        for m in OPEN_ORDER:
            for n in OPEN_ORDER:
                for a in s[m]['shop']:
                    for b in s[n]['door']:
                        d = cost[a][b]
                        if m == n:
                            if m == 'C':
                                total['c120'][0] += d >= 120
                                total['c120'][1] += 1
                        else:
                            total['x300'][0] += d >= 300
                            total['x300'][1] += 1
                            if {m, n} == {'C', 'A'}:
                                total['ca220'][0] += d >= 220
                                total['ca220'][1] += 1
        ratio = {k: v[0] / v[1] for k, v in total.items() if v[1]}
        return 2 * ratio['c120'] + 2 * ratio['ca220'] + 4 * ratio['x300'] - penalty

    current = score(state)
    best, best_state = current, copy.deepcopy(state)
    for step in range(SEARCH_STEPS):
        temperature = max(.002, .08 * (1 - step / SEARCH_STEPS))
        z = rng.choice(OPEN_ORDER)
        kind = rng.choice(('shop', 'door', 'door'))
        slot = rng.randrange(len(state[z][kind]))
        old = state[z][kind][slot]
        state[z][kind][slot] = rng.choice(shop_pool[z] if kind == 'shop' else door_pool[z])
        value = score(state)
        if value >= current or rng.random() < math.exp((value - current) / temperature):
            current = value
            if value > best:
                best, best_state = value, copy.deepcopy(state)
        else:
            state[z][kind][slot] = old
    return best_state, best


def ratios(values):
    n = len(values)
    return {'count': n, 'min': round(min(values)), 'mean': round(sum(values) / n), 'max': round(max(values)),
            **{f'at{t}': round(sum(v >= t for v in values) / n, 3) for t in THRESHOLDS}}


def picture(path, layout, anchors, zones_json, report):
    KOREAN = next(p for p in ('C:/Windows/Fonts/malgun.ttf', '/mnt/c/Windows/Fonts/malgun.ttf') if Path(p).exists())
    BOLD = next(p for p in ('C:/Windows/Fonts/malgunbd.ttf', '/mnt/c/Windows/Fonts/malgunbd.ttf') if Path(p).exists())
    def font(size, bold=False):
        return ImageFont.truetype(BOLD if bold else KOREAN, size)
    px, margin, panel = 7.0, 40, 560
    W, H = round(BOUNDS_SIZE[0] * px) + margin * 2 + panel, round(BOUNDS_SIZE[1] * px) + margin * 2
    im = Image.new('RGB', (W, H), '#f4f3ef')
    draw = ImageDraw.Draw(im, 'RGBA')
    def xy(x, z):
        return (margin + (x - BOUNDS_MIN[0]) * px, margin + (BOUNDS_MIN[1] + BOUNDS_SIZE[1] - z) * px)
    fills = {'A': '#efd9c2', 'B': '#cfdeec', 'C': '#d3e6cd', 'D': '#e3d3e6'}
    for z in zones_json['zones']:
        draw.polygon([xy(*p) for p in z['polygon']], fill=fills[z['id']])
    old_count = report['existingRoadCount']
    def road(r, color):
        a, b = xy(*r['a']), xy(*r['b'])
        width = max(2, round(r['width'] * px))
        draw.line([a, b], fill=color, width=width)
        for x, y in (a, b):
            draw.ellipse((x - width / 2, y - width / 2, x + width / 2, y + width / 2), fill=color)
    for r in layout['roads'][old_count:]:
        road(r, '#e8853a')
    for r in layout['roads'][:old_count]:
        road(r, '#4d5560')
    for lot in layout['lots']:
        _, _, x, z, w, d, _, angle = lot
        if angle % 180:
            w, d = d, w
        draw.rectangle([xy(x - w / 2, z + d / 2), xy(x + w / 2, z - d / 2)], fill='#8c8f94', outline='#5d6066')
    # Old map outline, dashed, so the old shape reads at a glance.
    corners = [(-10, 9), (125, 9), (125, -110), (-10, -110)]
    for i in range(4):
        a, b = xy(*corners[i]), xy(*corners[(i + 1) % 4])
        length = routes.dist(a, b)
        for s in range(0, int(length), 22):
            t0, t1 = s / length, min(1, (s + 12) / length)
            draw.line([(a[0] + (b[0] - a[0]) * t0, a[1] + (b[1] - a[1]) * t0), (a[0] + (b[0] - a[0]) * t1, a[1] + (b[1] - a[1]) * t1)], fill='#20242a', width=4)
    for m, n, p, q in border_edges():
        draw.line([xy(*p), xy(*q)], fill='#1b1b1b', width=5)
    for s in zones_json['barriers']:
        x, y = xy(*s['position'])
        draw.rectangle((x - 9, y - 9, x + 9, y + 9), fill='#d22b2b', outline='#ffffff', width=2)
        draw.line((x - 5, y - 5, x + 5, y + 5), fill='#ffffff', width=3)
        draw.line((x - 5, y + 5, x + 5, y - 5), fill='#ffffff', width=3)
    for z in zones_json['zones']:
        x, y = xy(*z['label'])
        draw.text((x, y - 34), z['id'], font=font(92, True), fill='#1f2933', anchor='mm', stroke_width=5, stroke_fill='#ffffff')
        draw.text((x, y + 44), f"{z['area']:,.0f}㎡", font=font(38, True), fill='#1f2933', anchor='mm', stroke_width=4, stroke_fill='#ffffff')
    small = font(21, True)
    for a in anchors:
        x, y = xy(a['position'][0], a['position'][2])
        kept = a.get('building') is not None
        if a['type'] == 'shop':
            r = 15
            draw.rectangle((x - r, y - r, x + r, y + r), fill='#f0b400' if kept else '#ffffff', outline='#7a5b00', width=4)
            draw.text((x, y), a['zone'], font=small, fill='#3a2c00', anchor='mm')
        elif a['type'] == 'door':
            r = 11
            draw.ellipse((x - r, y - r, x + r, y + r), fill='#1f6fc4' if kept else '#ffffff', outline='#0d3f73', width=4)
        else:
            draw.polygon([(x, y - 24), (x - 22, y), (x - 15, y), (x - 15, y + 20), (x + 15, y + 20), (x + 15, y), (x + 22, y)], fill='#111111', outline='#ffffff')
    # Scale bar, bottom left of the map.
    x0, y0 = xy(-84, -147)
    draw.line((x0, y0, x0 + 50 * px, y0), fill='#111111', width=8)
    for s in (0, 25, 50):
        draw.line((x0 + s * px, y0 - 12, x0 + s * px, y0 + 12), fill='#111111', width=4)
        draw.text((x0 + s * px, y0 - 30), f'{s}', font=font(24, True), fill='#111111', anchor='mm', stroke_width=3, stroke_fill='#ffffff')
    draw.text((x0 + 50 * px + 26, y0 - 30), 'm', font=font(24, True), fill='#111111', anchor='mm', stroke_width=3, stroke_fill='#ffffff')
    # Side panel.
    left = margin * 2 + round(BOUNDS_SIZE[0] * px) + 8
    y = margin
    draw.text((left, y), '맵 넓히기 도로안', font=font(40, True), fill='#111111')
    y += 62
    draw.text((left, y), f"전체 {BOUNDS_SIZE[0]:.0f}×{BOUNDS_SIZE[1]:.0f}m, 기존 135×119m는 점선", font=font(23), fill='#333333')
    y += 54
    items = [('road_old', '기존 길'), ('road_new', '새 길'), ('lot', '기존 건물 52채 (새 건물은 다음 단계)'), ('border', '구역 경계'),
             ('barrier', f"막이 자리 {len(zones_json['barriers'])}곳"), ('shop_old', '가게: 기존 자리'), ('shop_new', '가게: 새 자리'),
             ('door_old', '문: 기존 자리'), ('door_new', '문: 새 자리'), ('home', '집')]
    for key, text in items:
        cx, cy = left + 24, y + 16
        if key == 'road_old':
            draw.rectangle((left, cy - 8, left + 48, cy + 8), fill='#4d5560')
        elif key == 'road_new':
            draw.rectangle((left, cy - 8, left + 48, cy + 8), fill='#e8853a')
        elif key == 'lot':
            draw.rectangle((cx - 16, cy - 12, cx + 16, cy + 12), fill='#8c8f94', outline='#5d6066')
        elif key == 'border':
            draw.line((left, cy, left + 48, cy), fill='#1b1b1b', width=5)
        elif key == 'barrier':
            draw.rectangle((cx - 9, cy - 9, cx + 9, cy + 9), fill='#d22b2b', outline='#ffffff', width=2)
        elif key.startswith('shop'):
            draw.rectangle((cx - 13, cy - 13, cx + 13, cy + 13), fill='#f0b400' if key == 'shop_old' else '#ffffff', outline='#7a5b00', width=4)
        elif key.startswith('door'):
            draw.ellipse((cx - 11, cy - 11, cx + 11, cy + 11), fill='#1f6fc4' if key == 'door_old' else '#ffffff', outline='#0d3f73', width=4)
        else:
            draw.polygon([(cx, cy - 15), (cx - 15, cy), (cx - 10, cy), (cx - 10, cy + 13), (cx + 10, cy + 13), (cx + 10, cy), (cx + 15, cy)], fill='#111111')
        draw.text((left + 66, y), text, font=font(24), fill='#222222')
        y += 42
    y += 22
    head = font(25, True)
    body = font(24)
    def table(title, header, rows, columns):
        nonlocal y
        draw.text((left, y), title, font=font(29, True), fill='#111111')
        y += 44
        for col, text in zip(columns, header):
            draw.text((left + col, y), text, font=head, fill='#333333')
        y += 36
        draw.line((left, y, left + panel - 60, y), fill='#888888', width=2)
        y += 8
        for row in rows:
            for col, text in zip(columns, row):
                draw.text((left + col, y), str(text), font=body, fill='#222222')
            y += 34
        y += 22
    area_rows = [(k, f"{report['zones'][k]['current']:,}", f"{report['zones'][k]['area']:,.0f}", f"{report['zones'][k]['roadLength']:,}", report['zones'][k]['moved']) for k in 'ABCD']
    table('구역 (목표 16,000㎡ ±30%)', ('구역', '현재㎡', '넓힌㎡', '길 m', '옮긴 곳'), area_rows, (0, 70, 190, 310, 410))
    def pct(v):
        return f'{v * 100:.0f}%'
    distance = report['distance']
    rows = [(k, f"{distance[k]['mean']}m", pct(distance[k]['at120']), pct(distance[k]['at220']), pct(distance[k]['at300'])) for k in ('same', 'neighbor', 'far', 'all_other')]
    names = {'same': '같은 구역', 'neighbor': '맞닿은 쌍', 'far': 'A-D (먼 쌍)', 'all_other': '다른 구역 전체'}
    rows = [(names[r[0]], *r[1:]) for r in rows]
    table('가게→문 거리', ('구분', '평균', '120+', '220+', '300+'), rows, (0, 210, 300, 380, 460))
    im.save(path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--picture')
    parser.add_argument('--keep-anchors', action='store_true', help='reuse anchors_expanded.json instead of searching again')
    args = parser.parse_args()
    d = routes.directory
    layout = routes.read_json('layout.json')
    anchors_old = routes.read_json('anchors.json')
    old_count = len(layout['roads'])
    new = [{'id': i, 'a': list(a), 'b': list(b), 'width': w} for i, a, b, w in NEW_ROADS]
    all_roads = layout['roads'] + new
    expanded = dict(layout)
    expanded['roads'] = all_roads
    expanded['expansion_note'] = 'layout.json roads and lots unchanged; new roads follow. New lots come in a later step. Built by Tools/build_myeongji_expansion.py.'
    expanded['existingRoadCount'] = old_count
    # Sanity of the plan itself.
    clash = check_lots_clear(new, layout['lots'])
    if clash:
        raise RuntimeError(f'New road or sidewalk crosses an existing building: {clash}')
    edges = border_edges()
    border_length = {}
    for m, n, p, q in edges:
        border_length[m + n] = border_length.get(m + n, 0) + routes.dist(p, q)
    total_border = sum(border_length.values())
    spots = barriers(all_roads)
    current = {'A': 2257, 'B': 4678, 'C': 4802, 'D': 4328}
    zones_json = {'bounds': {'minimum': list(BOUNDS_MIN), 'size': list(BOUNDS_SIZE)}, 'openOrder': OPEN_ORDER, 'home': 'C',
                  'zones': [{'id': k, 'polygon': [list(p) for p in ZONES[k]], 'label': list(LABELS[k]), 'area': round(polygon_area(ZONES[k]))} for k in ZONES],
                  'borders': [{'zones': [m, n], 'from': [round(v, 2) for v in p], 'to': [round(v, 2) for v in q]} for m, n, p, q in edges],
                  'barriers': spots}
    (d / 'layout_expanded.json').write_text(json.dumps(expanded, ensure_ascii=False, indent=1), encoding='utf8')
    (d / 'zones.json').write_text(json.dumps(zones_json, ensure_ascii=False, indent=1), encoding='utf8')
    # Existing anchors must sit in their own zone polygon, or they cannot stay.
    misplaced = [(a['name'], a['zone'], zone_at(a['position'][0], a['position'][2])) for a in anchors_old if a['zone'] != zone_at(a['position'][0], a['position'][2])]
    if args.keep_anchors:
        anchors = routes.read_json('anchors_expanded.json')
    else:
        old_clean = [a for a in anchors_old if a['type'] != 'home' and (a['name'], a['zone'], zone_at(a['position'][0], a['position'][2])) not in misplaced]
        cands = new_candidates(new, all_roads)
        pool = old_clean + cands
        home = next(a for a in anchors_old if a['type'] == 'home')
        graph_anchors = pool + [home]
        for i, c in enumerate(pool):
            c.setdefault('name', f'cand_{i}')
        nodes, graph = routes.build_graph(all_roads, graph_anchors)
        print('candidates', len(cands), 'nodes', len(nodes), flush=True)
        costs = {a['node']: routes.distances(graph, a['node']) for a in pool}
        cost = [[costs[a['node']][b['node']] for b in pool] for a in pool]
        samples = {z: [] for z in OPEN_ORDER}
        for r in all_roads:
            length = routes.dist(r['a'], r['b'])
            for s in range(0, int(length), 10):
                p = (r['a'][0] + (r['b'][0] - r['a'][0]) * s / length, r['a'][1] + (r['b'][1] - r['a'][1]) * s / length)
                z = zone_at(*p)
                if z:
                    samples[z].append(p)
        by_zone = lambda kind: {z: [a for a in old_clean if a['type'] == kind and a['zone'] == z] for z in OPEN_ORDER}
        # A slot whose old anchor had to go still needs a start.
        start_shops, start_doors = by_zone('shop'), by_zone('door')
        for z in OPEN_ORDER:
            spare = [c for c in cands if c['zone'] == z]
            while len(start_shops[z]) < 3:
                start_shops[z].append(spare.pop(0))
            while len(start_doors[z]) < 8:
                start_doors[z].append(spare.pop(0))
        home_cost = [costs[a['node']][home['node']] for a in pool]
        road_width = {r['id']: r['width'] for r in all_roads}
        best_state, value = search(pool, start_shops, start_doors, cost, samples, home_cost, road_width)
        print('search score', round(value, 3), flush=True)
        anchors = []
        # A kept anchor stays in its own slot, so its shop name still belongs to its building.
        waiting = {(z, kind): [pool[i] for i in best_state[z][kind]] for z in OPEN_ORDER for kind in ('shop', 'door')}
        own = {c['name']: c for group in waiting.values() for c in group if c['type'] != 'new'}
        for key in waiting:
            waiting[key] = [c for c in waiting[key] if c['type'] == 'new']
        for a in anchors_old:
            if a['type'] == 'home':
                anchors.append({k: v for k, v in a.items() if k != 'node'})
                continue
            z = a['zone']
            picked = own[a['name']] if a['name'] in own else waiting[(z, a['type'])].pop(0)
            entry = {'name': a['name'], 'type': a['type'], 'index': a['index'], 'zone': z, 'building': picked.get('building'),
                     'position': picked['position'], 'forward': picked['forward'], 'road': picked['road'], 'roadPoint': picked['roadPoint'], 'shopName': a['shopName']}
            anchors.append(entry)
        # Keep the original list order (shops, doors, home by type and index).
        (d / 'anchors_expanded.json').write_text(json.dumps(anchors, ensure_ascii=False, indent=1), encoding='utf8')
    anchors = [dict(a) for a in anchors]
    nodes, graph = routes.build_graph(all_roads, anchors)
    connected = len(routes.distances(graph, 0)) == len(nodes)
    shops = [a for a in anchors if a['type'] == 'shop']
    doors = [a for a in anchors if a['type'] == 'door']
    home = next(a for a in anchors if a['type'] == 'home')
    table = {a['node']: routes.distances(graph, a['node']) for a in anchors}
    # Distances with closed zones cut out: the table is global, the rider is not.
    node_zone = {}
    def edge_open(i, j, opened):
        a, b = nodes[i], nodes[j]
        return all(zone_at(a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t) in opened for t in (0, .25, .5, .75, 1))
    def closed_graph(opened):
        return {i: {j: w for j, w in e.items() if edge_open(i, j, opened)} for i, e in graph.items()}
    days = {}
    for day, opened in enumerate((['C'], ['C', 'A'], ['C', 'A', 'B'], ['C', 'A', 'B', 'D']), 1):
        limited = closed_graph(opened)
        longer = unreachable = pairs = 0
        for s in shops:
            if s['zone'] not in opened:
                continue
            reach = routes.distances(limited, s['node'])
            for o in doors:
                if o['zone'] not in opened:
                    continue
                pairs += 1
                if o['node'] not in reach:
                    unreachable += 1
                elif reach[o['node']] > table[s['node']][o['node']] + .5:
                    longer += 1
        days[day] = {'open': opened, 'pairs': pairs, 'longerThanTable': longer, 'unreachable': unreachable}
    pair_values = {m + n: [table[s['node']][o['node']] for s in shops if s['zone'] == m for o in doors if o['zone'] == n] for m in 'ABCD' for n in 'ABCD'}
    same = [v for k in pair_values if k[0] == k[1] for v in pair_values[k]]
    other = [v for k in pair_values if k[0] != k[1] for v in pair_values[k]]
    neighbor = [v for k in pair_values if k[0] != k[1] and set(k) != {'A', 'D'} for v in pair_values[k]]
    far = [v for k in pair_values if set(k) == {'A', 'D'} for v in pair_values[k]]
    moved = {z: sum(a['zone'] == z and a['type'] != 'home' and a.get('building') is None for a in anchors) for z in 'ABCD'}
    road_length = {}
    for r in all_roads:
        length = routes.dist(r['a'], r['b'])
        n = max(1, int(length))
        for t in range(n):
            z = zone_at(r['a'][0] + (r['b'][0] - r['a'][0]) * (t + .5) / n, r['a'][1] + (r['b'][1] - r['a'][1]) * (t + .5) / n)
            road_length[z] = road_length.get(z, 0) + length / n
    spacing, junctions = junction_spacing(all_roads)
    report = {
        'existingRoadCount': old_count, 'newRoadCount': len(new),
        'newRoadLength': round(sum(routes.dist(r['a'], r['b']) for r in new)), 'existingRoadLength': round(sum(routes.dist(r['a'], r['b']) for r in layout['roads'])),
        'connected': connected, 'nodes': len(nodes), 'edges': sum(map(len, graph.values())) // 2,
        'zones': {k: {'current': current[k], 'area': round(polygon_area(ZONES[k])), 'added': round(polygon_area(ZONES[k])) - current[k], 'roadLength': round(road_length[k]), 'moved': moved[k],
                      'kept': sum(a['zone'] == k and a['type'] != 'home' and a.get('building') is not None for a in anchors)} for k in 'ABCD'},
        'totalArea': round(sum(polygon_area(p) for p in ZONES.values())),
        'distance': {'same': ratios(same), 'neighbor': ratios(neighbor), 'far': ratios(far), 'all_other': ratios(other)},
        'pairs': {k: ratios(v) for k, v in pair_values.items()},
        'days': days,
        'homeToShopsC': [round(table[home['node']][s['node']]) for s in shops if s['zone'] == 'C'],
        'barrierCount': len(spots), 'borderLengthMeters': {k: round(v) for k, v in border_length.items()}, 'borderTotalMeters': round(total_border),
        'misplacedOldAnchors': misplaced,
        'closestTrafficJunctions': {'meters': round(spacing[0], 1), 'a': [round(v, 1) for v in spacing[1]], 'b': [round(v, 1) for v in spacing[2]], 'count': junctions},
    }
    (d / 'expansion_report.json').write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding='utf8')
    if args.picture:
        picture(args.picture, expanded, anchors, zones_json, report)
    print(json.dumps({k: report[k] for k in ('connected', 'totalArea', 'newRoadCount', 'newRoadLength', 'barrierCount', 'borderTotalMeters', 'misplacedOldAnchors', 'closestTrafficJunctions', 'homeToShopsC')}, ensure_ascii=False))


if __name__ == '__main__':
    main()
