"""Bake additional Myeongji buildings and lamps without opening Unity.

python -B Tools/build_myeongji_lots.py --self-test
python -B Tools/build_myeongji_lots.py --picture PATH --report-json PATH
python -B Tools/build_myeongji_lots.py --check-only
python -B Tools/build_myeongji_lots.py --update-anchors

--update-anchors follows moved shops and doors while keeping every other saved building and lamp where it is. A full
run with no option re-plans all of them.

Outputs are ADDITIONAL objects only: retain the original 52 buildings/34 lamps.
sourceBuildingId is the original lot ID, not its shared style string. width/depth
are source-local dimensions; rotationDeg is absolute layout heading (0 faces -Z,
90 +X, 180 +Z, 270 -X). Clone an existing baked mesh about its bounds centre and
rotate by target heading minus source lot heading; never rescale the mesh.
Geometry/checking uses only the standard library; --picture also needs Pillow.
"""
import argparse
from collections import Counter
import copy
import hashlib
import json
import math
from pathlib import Path
import re
import statistics
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'Docs/MapLayout/Myeongji'
SIDEWALK = 2.0
BORDER_GAP = .25
POST_RADIUS = .2  # Existing MapImport post collider is 0.4 x 0.4 m.
ANCHOR_DEPTH = 8.0
EPS = 1e-5
# The hand-corrected original walkers' lanes were not baked around the new buildings; a walker keeps left or right of a
# lane by 0.35 m and is 0.27 m wide.
LANE_CLEARANCE = .75
LANE_CELL = 4
FRONTS = {0: (0, -1), 90: (1, 0), 180: (0, 1), 270: (-1, 0)}


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def file_hash(path):
    """Line endings differ between checkouts; the data does not."""
    return hashlib.sha256(Path(path).read_bytes().replace(b'\r\n', b'\n')).hexdigest()


def distance(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def project(p, a, b):
    dx, dz = b[0] - a[0], b[1] - a[1]
    t = max(0, min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dz) / (dx * dx + dz * dz)))
    return a[0] + t * dx, a[1] + t * dz


def point_segment_distance(p, a, b):
    return distance(p, project(p, a, b))


def corners(box):
    x0, z0, x1, z1 = box
    return [(x0, z0), (x1, z0), (x1, z1), (x0, z1)]


def point_box_distance(p, box):
    return math.hypot(max(box[0] - p[0], 0, p[0] - box[2]), max(box[1] - p[1], 0, p[1] - box[3]))


def box_distance(a, b):
    return math.hypot(max(a[0] - b[2], b[0] - a[2], 0), max(a[1] - b[3], b[1] - a[3], 0))


def overlap(a, b, gap=0):
    return a[0] < b[2] + gap - EPS and b[0] < a[2] + gap - EPS and a[1] < b[3] + gap - EPS and b[1] < a[3] + gap - EPS


def segment_box_distance(a, b, box):
    """Exact segment/AABB distance, including segment endpoints and oblique corners."""
    lo, hi = 0.0, 1.0
    for index in (0, 1):
        delta = b[index] - a[index]
        if abs(delta) < 1e-12:
            if not box[index] <= a[index] <= box[index + 2]:
                break
        else:
            t0, t1 = sorted(((box[index] - a[index]) / delta, (box[index + 2] - a[index]) / delta))
            lo, hi = max(lo, t0), min(hi, t1)
            if lo > hi:
                break
    else:
        return 0.0
    return min(point_box_distance(a, box), point_box_distance(b, box),
               *(point_segment_distance(p, a, b) for p in corners(box)))


def inside(point, polygon):
    x, z = point
    found = False
    for a, b in zip(polygon, polygon[1:] + polygon[:1]):
        if (a[1] > z) != (b[1] > z) and x < (b[0] - a[0]) * (z - a[1]) / (b[1] - a[1]) + a[0]:
            found = not found
    return found


def box_in_zone(box, polygon, clearance=BORDER_GAP):
    if not all(inside(p, polygon) for p in corners(box)):
        return False
    # Corners alone miss a concave border entering/exiting the rectangle.
    return all(segment_box_distance(a, b, box) > clearance - EPS
               for a, b in zip(polygon, polygon[1:] + polygon[:1]))


def heading(front):
    return min(FRONTS, key=lambda r: distance(front, FRONTS[r]))


def footprint(lot):
    w, d = lot['width'], lot['depth']
    if lot['rotationDeg'] % 180:
        w, d = d, w
    return lot['x'] - w / 2, lot['z'] - d / 2, lot['x'] + w / 2, lot['z'] + d / 2


def lot_from_row(row, zones):
    ident, _, x, z, w, d, _, rot = row
    zone = next(zone['id'] for zone in zones['zones'] if inside((x, z), zone['polygon']))
    return dict(id=ident, sourceBuildingId=ident, x=x, z=z, rotationDeg=rot, zone=zone, width=w, depth=d, facesAnchor=None)


def posts_from_prefab():
    """Read collider coordinates; apply saved scene translation overrides, never edit YAML.

    This export has identity Furniture under a 180-degree Map root. Check that
    assumption before converting its local coordinates to world X/Z.
    """
    path = ROOT / 'Assets/_Game/Prefabs/Myeongji/MyeongjiMap.prefab'
    text = path.read_text(encoding='utf-8')
    blocks = re.split(r'^--- !u!', text, flags=re.M)[1:]
    names, transforms, stripped, instances = {}, {}, {}, {}
    vector = lambda s: {k: float(v) for k, v in re.findall(r'([xyzw]): ([-\d.eE+]+)', s)}
    for b in blocks:
        kind, ident = map(int, re.match(r'(\d+) &(-?\d+)', b).groups())
        match = re.search(r'^  m_Name: (.+)$', b, re.M)
        if kind == 1 and match:
            names[ident] = match[1]
        if kind == 1001:
            instances[ident] = b
        if kind == 4 and 'm_LocalPosition:' in b:
            transforms[ident] = dict(
                go=int(re.search(r'm_GameObject: \{fileID: (\d+)\}', b)[1]),
                parent=int(re.search(r'm_Father: \{fileID: (\d+)\}', b)[1]),
                pos=vector(re.search(r'm_LocalPosition: (.+)', b)[1]),
                rot=vector(re.search(r'm_LocalRotation: (.+)', b)[1]),
                scale=vector(re.search(r'm_LocalScale: (.+)', b)[1]))
        elif kind == 4:
            stripped[ident] = int(re.search(r'm_PrefabInstance: \{fileID: (\d+)\}', b)[1])
    root = next(t for t in transforms.values() if names.get(t['go']) == 'MyeongjiMap')
    if root['pos'] != dict(x=0, y=0, z=0) or root['scale'] != dict(x=1, y=1, z=1) or abs(root['rot']['y']) != 1 or root['rot']['w'] != 0:
        raise ValueError('Map root transform changed; update the read-only furniture coordinate conversion')
    post_ids = [i for i, t in transforms.items() if names.get(t['go'], '').endswith('_PostCollider')]
    for parent in {transforms[i]['parent'] for i in post_ids}:
        block = instances[stripped[parent]]
        source = re.search(r'm_CorrespondingSourceObject: \{fileID: (-?\d+)', next(b for b in blocks if b.startswith(f'4 &{parent} ')))[1]
        for axis, expected in [('m_LocalPosition.x', 0), ('m_LocalPosition.y', 0), ('m_LocalPosition.z', 0),
                               ('m_LocalRotation.x', 0), ('m_LocalRotation.y', 0), ('m_LocalRotation.z', 0), ('m_LocalRotation.w', 1)]:
            match = re.search(r'target: \{fileID: ' + source + r',[^\n]+\}\n\s+propertyPath: ' + re.escape(axis) + r'\n\s+value: (\S+)', block)
            if match is None or float(match[1]) != expected:
                raise ValueError('Furniture transform changed: ' + axis)
    prefab_guid = re.search(r'^guid: (\w+)', Path(str(path) + '.meta').read_text(), re.M)[1]
    scene = (ROOT / 'Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity').read_text(encoding='utf-8-sig')
    for ident, axis, val in re.findall(r'target: \{fileID: (-?\d+), guid: ' + prefab_guid + r', type: 3\}\n\s+propertyPath: m_LocalPosition\.([xz])\n\s+value: (\S+)', scene):
        if int(ident) in transforms:
            transforms[int(ident)]['pos'][axis] = float(val)
    return [dict(name=names[transforms[i]['go']], x=-transforms[i]['pos']['x'], z=-transforms[i]['pos']['z']) for i in post_ids]


def load_inputs():
    layout = read_json(DATA / 'layout_expanded.json')
    original = read_json(DATA / 'layout.json')
    zones = read_json(DATA / 'zones.json')
    anchors = read_json(DATA / 'anchors_expanded.json')
    if layout['lots'] != original['lots'] or layout['roads'][:len(original['roads'])] != original['roads']:
        raise ValueError('Expanded inputs must preserve original lots and roads')
    sources = {r[0]: r for r in original['lots']}
    if len(sources) != 52:
        raise ValueError('Expected 52 original building IDs')
    roads = []
    for road in layout['roads']:
        r = dict(road)
        length = distance(r['a'], r['b'])
        if length <= EPS or r['width'] <= 0:
            raise ValueError('Invalid road: ' + r['id'])
        r['length'] = length
        r['unit'] = ((r['b'][0] - r['a'][0]) / length, (r['b'][1] - r['a'][1]) / length)
        reach = r['width'] / 2 + SIDEWALK
        r['bounds'] = (min(r['a'][0], r['b'][0]) - reach, min(r['a'][1], r['b'][1]) - reach,
                       max(r['a'][0], r['b'][0]) + reach, max(r['a'][1], r['b'][1]) + reach)
        roads.append(r)
    existing = [lot_from_row(row, zones) for row in original['lots']]
    lanes = {}
    lane_file = DATA / 'pedestrian_original_paths.json'
    if lane_file.exists():
        for point in read_json(lane_file)['points']:
            lanes.setdefault((point[0] // LANE_CELL, point[1] // LANE_CELL), []).append(tuple(point))
    return dict(layout=layout, original=original, sources=sources, zones=zones, anchors=anchors, roads=roads,
                existing=existing, posts=posts_from_prefab(), lanes=lanes)


def blocks_lane(box, data):
    x0, z0, x1, z1 = box
    for cx in range(int((x0 - LANE_CLEARANCE) // LANE_CELL), int((x1 + LANE_CLEARANCE) // LANE_CELL) + 1):
        for cz in range(int((z0 - LANE_CLEARANCE) // LANE_CELL), int((z1 + LANE_CLEARANCE) // LANE_CELL) + 1):
            if any(point_box_distance(point, box) < LANE_CLEARANCE - EPS for point in data['lanes'].get((cx, cz), ())):
                return True
    return False


def measure(data):
    old_roads = data['roads'][:len(data['original']['roads'])]
    new_roads = data['roads'][len(old_roads):]
    old_boxes = [footprint(lot) for lot in data['existing']]
    setbacks = [min(segment_box_distance(r['a'], r['b'], b) - r['width'] / 2 for r in old_roads) for b in old_boxes]
    gaps = [min(box_distance(b, other) for j, other in enumerate(old_boxes) if i != j) for i, b in enumerate(old_boxes)]
    lamps = [p for p in data['posts'] if p['name'].startswith('StreetLamp_')]
    lamp_steps = [min(distance((p['x'], p['z']), (q['x'], q['z'])) for q in lamps if p != q) for p in lamps]
    # Original plan bounding rectangle, the same dashed outline as the approved road picture.
    old_area = 135 * 119
    density = len(old_boxes) / old_area
    targets = {z['id']: round((z['area'] - current) * density) for z, current in
               zip(data['zones']['zones'], (2257, 4678, 4802, 4328))}
    lamp_target = round(len(lamps) * sum(r['length'] for r in new_roads) / sum(r['length'] for r in old_roads))
    return dict(densityPer1000=density * 1000, oldArea=old_area, targets=targets, lampTarget=lamp_target,
                oldLampCount=len(lamps), oldRoadLength=sum(r['length'] for r in old_roads),
                addedRoadLength=sum(r['length'] for r in new_roads),
                measuredSetback=round(statistics.median(setbacks), 4),
                setback=max(SIDEWALK, round(statistics.median(setbacks), 2)),
                measuredGap=round(statistics.median(gaps), 4), gap=round(statistics.median(gaps), 2),
                anchorMinGap=round(min(gaps), 2),
                measuredLampNearest=round(statistics.median(lamp_steps), 4),
                lampNearestMin=round(min(lamp_steps), 4), lampNearestMax=round(max(lamp_steps), 4))


def access_for(lot, road):
    front = FRONTS[lot['rotationDeg']]
    a = (lot['x'] + front[0] * (lot['depth'] / 2 + .05), lot['z'] + front[1] * (lot['depth'] / 2 + .05))
    b = project(a, road['a'], road['b'])
    return dict(lotId=lot['id'], road=road['id'], start=list(a), end=list(b), clearWidth=SIDEWALK)


class Planner:
    def __init__(self, data, metrics):
        self.data, self.metrics = data, metrics
        self.polygons = {z['id']: z['polygon'] for z in data['zones']['zones']}
        self.old_boxes = [footprint(lot) for lot in data['existing']]
        self.boxes, self.lots, self.access = [], [], []
        self.usage = Counter()
        self.zone_counts = Counter()
        self.anchor_paths = []
        for a in data['anchors']:
            p, q = a['position'], a['roadPoint']
            self.anchor_paths.append(((p[0], p[2]), (q[0], q[2])))

    def fits(self, lot, road, anchored=False):
        box = footprint(lot)
        if not box_in_zone(box, self.polygons[lot['zone']]) or blocks_lane(box, self.data):
            return False
        for r in self.data['roads']:
            if overlap(box, r['bounds']) and segment_box_distance(r['a'], r['b'], box) < r['width'] / 2 + SIDEWALK - EPS:
                return False
        required_gap = self.metrics['anchorMinGap'] if anchored else self.metrics['gap']
        if any(box_distance(box, old) < required_gap - EPS for old in self.old_boxes + self.boxes):
            return False
        if any(point_box_distance((p['x'], p['z']), box) < POST_RADIUS + .1 for p in self.data['posts']):
            return False
        if any(segment_box_distance(a, b, box) < SIDEWALK / 2 - EPS for a, b in self.anchor_paths):
            return False
        path = access_for(lot, road)
        if not anchored and any(segment_box_distance(path['start'], path['end'], b) < SIDEWALK / 2 - EPS for b in self.old_boxes + self.boxes):
            return False
        if any(segment_box_distance(p['start'], p['end'], box) < SIDEWALK / 2 - EPS for p in self.access):
            return False
        # Same exact source must not be a direct neighbour; the shared style name may repeat.
        for other in self.data['existing'] + self.lots:
            if other['sourceBuildingId'] == lot['sourceBuildingId'] and box_distance(box, footprint(other)) < 12:
                return False
        return True

    def make_lot(self, source, x, z, rotation, zone, anchor=None):
        row = self.data['sources'][source]
        return dict(id=f"{zone}_Expanded_{self.zone_counts[zone] + 1:03d}", sourceBuildingId=source,
                    x=round(x, 4), z=round(z, 4), rotationDeg=rotation, zone=zone, width=row[4], depth=row[5], facesAnchor=anchor)

    def add(self, lot, road):
        self.lots.append(lot)
        self.boxes.append(footprint(lot))
        self.usage[lot['sourceBuildingId']] += 1
        self.zone_counts[lot['zone']] += 1
        self.access.append(access_for(lot, road))

    def place_anchors(self):
        roads = {r['id']: r for r in self.data['roads']}
        moved = [a for a in self.data['anchors'] if a.get('building') is None and a['type'] in ('shop', 'door')]
        # Narrow/corner plots first; deeper facades are necessary for the 8m reservation.
        def options(a):
            r, p = roads[a['road']], a['position']
            rot = heading((a['forward'][0], a['forward'][2]))
            fx, fz = FRONTS[rot]
            opts = []
            for source, row in self.data['sources'].items():
                if row[5] < ANCHOR_DEPTH - EPS:
                    continue
                for front_gap in (.6, .9, 1.2, 1.6, 2.0, 2.5, 3.5, 4.5, 6.0, 7.5, 9.0):
                    for lateral in (0, -.5, .5, -1, 1, -2, 2, -(row[4] / 2 - .5), row[4] / 2 - .5):
                        lot = self.make_lot(source, p[0] - fx * (row[5] / 2 + front_gap) - fz * lateral,
                                            p[2] - fz * (row[5] / 2 + front_gap) + fx * lateral, rot, a['zone'], a['name'])
                        if abs(lateral) <= row[4] / 2 - .5 and self.fits(lot, r, True):
                            opts.append((front_gap + abs(lateral) * .2, lot, r))
            return opts
        pending = [(a, [(cost, lot, road, footprint(lot), access_for(lot, road))
                        for cost, lot, road in options(a)]) for a in moved]
        print('anchor candidate counts', {a['name']: len(v) for a, v in pending}, flush=True)
        # Forward-check only the newly occupied rectangle. Remaining lists already
        # satisfy every previous choice, so recomputing them against all past plots
        # is unnecessary and greatly slows down the simultaneous corner solve.
        def conflict(first, second):
            _, lot, _, box, path = first
            _, other, _, other_box, other_path = second
            gap = box_distance(box, other_box)
            if gap < self.metrics['anchorMinGap'] - EPS:
                return True
            if lot['sourceBuildingId'] == other['sourceBuildingId'] and gap < 12:
                return True
            return (segment_box_distance(path['start'], path['end'], other_box) < SIDEWALK / 2 - EPS
                    or segment_box_distance(other_path['start'], other_path['end'], box) < SIDEWALK / 2 - EPS)

        def solve(remaining):
            if not remaining:
                return True
            anchor, opts = min(remaining, key=lambda v: (len(v[1]), v[0]['name']))
            if not opts:
                return False
            rest = [(a, v) for a, v in remaining if a['name'] != anchor['name']]
            opts = sorted(opts, key=lambda v: (v[0] + self.usage[v[1]['sourceBuildingId']] * 1.5, v[1]['sourceBuildingId']))
            for choice in opts:
                _, lot, road, _, _ = choice
                filtered = [(a, [v for v in variants if not conflict(choice, v)]) for a, variants in rest]
                if any(not v for _, v in filtered):
                    continue
                lot['id'] = f"{anchor['zone']}_Expanded_{self.zone_counts[anchor['zone']] + 1:03d}"
                self.add(lot, road)
                if solve(filtered):
                    return True
                self.lots.pop(); self.boxes.pop(); self.access.pop()
                self.usage[lot['sourceBuildingId']] -= 1
                self.zone_counts[lot['zone']] -= 1
            return False

        if not solve(pending):
            raise RuntimeError('No simultaneous legal frontage assignment for all 28 anchors')

    def residual_infill(self, minimum_use, step=2):
        """Search actual remaining land after frontage rows, including between rows.

        Road-relative rows are only candidate generators: their discrete offsets
        must not turn usable block interiors into artificial empty land.
        """
        sources = sorted((s for s in self.data['sources'] if self.usage[s] == minimum_use),
                         key=lambda s: (self.data['sources'][s][4] * self.data['sources'][s][5], s))
        for zone, target in self.metrics['targets'].items():
            if self.zone_counts[zone] >= target:
                continue
            poly = self.polygons[zone]
            points = []
            for ix in range(math.ceil(min(p[0] for p in poly)), math.floor(max(p[0] for p in poly)), step):
                for iz in range(math.ceil(min(p[1] for p in poly)), math.floor(max(p[1] for p in poly)), step):
                    point = (ix, iz)
                    if not inside(point, poly):
                        continue
                    clearance = min(point_box_distance(point, b) for b in self.old_boxes + self.boxes)
                    if clearance < 2.5:
                        continue
                    nearby = sorted(self.data['roads'], key=lambda r: point_segment_distance(point, r['a'], r['b']))[:5]
                    if any(point_segment_distance(point, r['a'], r['b']) < r['width'] / 2 + SIDEWALK + 2 for r in nearby):
                        continue
                    points.append((clearance, point, nearby))
            points.sort(key=lambda v: (-v[0], v[1]))
            for source in sources:
                for _, point, nearby in points:
                    for road in nearby:
                        q = project(point, road['a'], road['b'])
                        rot = heading((q[0] - point[0], q[1] - point[1]))
                        lot = self.make_lot(source, *point, rot, zone)
                        if self.fits(lot, road):
                            print('interior infill', lot['id'], source, point, flush=True)
                            return lot, road, 1, footprint(lot), access_for(lot, road)
        return None

    def fill(self):
        """Spread occupied frontage first, then fill wide blocks with accessible rear plots."""
        roads = self.data['roads'][len(self.data['original']['roads']):]
        sites = []
        for r in roads:
            ux, uz = r['unit']
            nx, nz = -uz, ux
            for along_index in range(1, int(r['length'] / 2)):
                along = along_index * 2
                q = (r['a'][0] + ux * along, r['a'][1] + uz * along)
                for side in (-1, 1):
                    rot = heading((-side * nx, -side * nz))
                    for row in (0, 1):
                        sites.append((r, q, nx * side, nz * side, rot, row))
        # Precompute immutable road/zone/old-building checks once, then retain only
        # candidates compatible with each newly occupied footprint/access corridor.
        candidates = []
        for road, q, nx, nz, rot, rear in sites:
            for source, r in self.data['sources'].items():
                w, d = (r[5], r[4]) if rot % 180 else (r[4], r[5])
                support = abs(nx) * w / 2 + abs(nz) * d / 2
                offset = road['width'] / 2 + self.metrics['setback'] + support + rear * (10 + self.metrics['gap']) + .02
                x, z = q[0] + nx * offset, q[1] + nz * offset
                zone = next((k for k, p in self.polygons.items() if inside((x, z), p)), None)
                if zone is None:
                    continue
                lot = self.make_lot(source, x, z, rot, zone)
                if self.fits(lot, road):
                    candidates.append((lot, road, rear, footprint(lot), access_for(lot, road)))
        print('legal fill candidates', len(candidates), flush=True)
        while any(self.zone_counts[z] < n for z, n in self.metrics['targets'].items()):
            min_use = min(self.usage[s] for s in self.data['sources'])
            feasible = [v for v in candidates if self.usage[v[0]['sourceBuildingId']] == min_use
                        and self.zone_counts[v[0]['zone']] < self.metrics['targets'][v[0]['zone']]]
            if not feasible:
                extra = self.residual_infill(min_use)
                if extra is None:
                    extra = self.residual_infill(min_use, step=1)
                if extra is None:
                    raise RuntimeError(f'Cannot meet balanced source/density targets: {dict(self.zone_counts)}, {dict(self.usage)}')
                feasible = [extra]
            def score(v):
                return min(box_distance(v[3], b) for b in self.old_boxes + self.boxes) - v[2] * 4
            # Reserve scarce large footprints before small houses consume them.
            # Reserve the zone with the least remaining choice per unfilled lot;
            # otherwise flexible northern plots can exhaust the small templates
            # needed for the narrower southern blocks.
            zone_capacity = Counter(v[0]['zone'] for v in feasible)
            zone = min(zone_capacity, key=lambda z: (zone_capacity[z] / (self.metrics['targets'][z] - self.zone_counts[z]), z))
            feasible = [v for v in feasible if v[0]['zone'] == zone]
            availability = Counter(v[0]['sourceBuildingId'] for v in feasible)
            source = min(availability, key=lambda k: (availability[k], -self.data['sources'][k][4] * self.data['sources'][k][5], k))
            feasible = [v for v in feasible if v[0]['sourceBuildingId'] == source]
            lot, road, rear, box, path = max(feasible, key=lambda v: (score(v), v[0]['x'], v[0]['z']))
            lot['id'] = f"{lot['zone']}_Expanded_{self.zone_counts[lot['zone']] + 1:03d}"
            self.add(lot, road)
            candidates = [v for v in candidates if box_distance(v[3], box) >= self.metrics['gap'] - EPS
                          and not (v[0]['sourceBuildingId'] == lot['sourceBuildingId'] and box_distance(v[3], box) < 12)
                          and segment_box_distance(path['start'], path['end'], v[3]) >= SIDEWALK / 2 - EPS
                          and segment_box_distance(v[4]['start'], v[4]['end'], box) >= SIDEWALK / 2 - EPS]
            if len(self.lots) % 20 == 0:
                print('placed', len(self.lots), dict(self.zone_counts), flush=True)


def lamp_sites(data, lots, access):
    roads = data['roads']
    boxes = [footprint(lot) for lot in data['existing'] + lots]
    old_points = [(p['x'], p['z']) for p in data['posts']]
    anchor_paths = [((a['position'][0], a['position'][2]), (a['roadPoint'][0], a['roadPoint'][2])) for a in data['anchors']]
    candidates = []
    for r in roads[len(data['original']['roads']):]:
        ux, uz = r['unit']
        nx, nz = -uz, ux
        for along in range(7, int(r['length']) - 6, 2):
            q = (r['a'][0] + ux * along, r['a'][1] + uz * along)
            for side in (-1, 1):
                offset = r['width'] / 2 + SIDEWALK + POST_RADIUS + .1
                p = (q[0] + side * nx * offset, q[1] + side * nz * offset)
                box = (p[0] - POST_RADIUS, p[1] - POST_RADIUS, p[0] + POST_RADIUS, p[1] + POST_RADIUS)
                if not any(box_in_zone(box, z['polygon']) for z in data['zones']['zones']):
                    continue
                if any(point_box_distance(p, b) < POST_RADIUS + .15 for b in boxes):
                    continue
                if any(segment_box_distance(s['a'], s['b'], box) < s['width'] / 2 + SIDEWALK - EPS for s in roads):
                    continue
                # Reserve every junction approach for future signals/crossings, including near-end road joins.
                if any(s is not r and point_segment_distance(q, s['a'], s['b']) < s['width'] / 2 + 5 for s in roads):
                    continue
                if any(point_segment_distance(p, a, b) < SIDEWALK / 2 + POST_RADIUS + .2 for a, b in anchor_paths):
                    continue
                if any(point_segment_distance(p, a['start'], a['end']) < SIDEWALK / 2 + POST_RADIUS + .2 for a in access):
                    continue
                # Existing three main-street crossings: width 4.7m plus a 2m safety margin.
                if any(abs(p[0] - x) < 4.35 and abs(p[1]) < 8 for x in (8, 36, 101)):
                    continue
                if min(distance(p, q) for q in old_points) < 3:
                    continue
                candidates.append(dict(x=round(p[0], 4), z=round(p[1], 4), road=r['id'],
                                       rotationDeg=round(math.degrees(math.atan2(-side * nx, side * nz)) % 360, 4)))
    return candidates


def place_lamps(data, metrics, lots, access):
    candidates = lamp_sites(data, lots, access)
    old_points = [(p['x'], p['z']) for p in data['posts']]
    result = []
    while len(result) < metrics['lampTarget']:
        if not candidates:
            raise RuntimeError('Not enough legal lamp sites')
        def score(lamp):
            return min(distance((lamp['x'], lamp['z']), p) for p in old_points)
        lamp = max(candidates, key=score)
        if score(lamp) < 5:
            raise RuntimeError('Lamps cannot meet density without clustering')
        result.append(lamp)
        point = (lamp['x'], lamp['z'])
        old_points.append(point)
        candidates = [p for p in candidates if distance((p['x'], p['z']), point) >= 5]
    return result


def verify(data, metrics, lots, lamps, access):
    """Recheck saved output independently of candidate selection; abort on any violation."""
    errors = []
    polygons = {z['id']: z['polygon'] for z in data['zones']['zones']}
    boxes = [footprint(lot) for lot in lots]
    old_boxes = [footprint(lot) for lot in data['existing']]
    roads = {r['id']: r for r in data['roads']}
    moved = {a['name']: a for a in data['anchors'] if a.get('building') is None and a['type'] in ('shop', 'door')}
    assigned = Counter(lot['facesAnchor'] for lot in lots if lot['facesAnchor'])
    if set(assigned) != set(moved) or any(n != 1 for n in assigned.values()):
        errors.append('Moved anchors must each have exactly one facade: ' + ', '.join(sorted(set(moved) ^ set(assigned))))
    if len({lot['id'] for lot in lots}) != len(lots):
        errors.append('Duplicate building IDs')
    if Counter(lot['zone'] for lot in lots) != Counter(metrics['targets']):
        errors.append('Zone density targets mismatch')
    usage = Counter(lot['sourceBuildingId'] for lot in lots)
    if set(usage) != set(data['sources']) or max(usage.values(), default=0) - min(usage.values(), default=0) > 1:
        errors.append('All 52 source IDs must be used evenly (max-min <= 1)')
    access_by_id = {a['lotId']: a for a in access}
    if len(access_by_id) != len(lots) or set(access_by_id) != {l['id'] for l in lots}:
        errors.append('One access path per lot required')
    for i, (lot, box) in enumerate(zip(lots, boxes)):
        ident = lot['id']
        source = data['sources'].get(lot['sourceBuildingId'])
        if source is None or lot['width'] != source[4] or lot['depth'] != source[5]:
            errors.append(ident + ': invalid or rescaled source')
        if lot['rotationDeg'] not in FRONTS:
            errors.append(ident + ': non-cardinal rotation')
            continue
        if lot['zone'] not in polygons or not box_in_zone(box, polygons[lot['zone']]):
            errors.append(ident + ': footprint crosses zone border')
        for r in roads.values():
            if segment_box_distance(r['a'], r['b'], box) < r['width'] / 2 + SIDEWALK - EPS:
                errors.append(ident + ': road/2m sidewalk overlap ' + r['id'])
        if any(overlap(box, b) for b in old_boxes + boxes[:i]):
            errors.append(ident + ': building overlap')
        if blocks_lane(box, data):
            errors.append(ident + ': on an original walking lane')
        # A building before an anchor may stand as close as the old map's tightest pair, on either side of the gap.
        required_gap = metrics['anchorMinGap'] if lot['facesAnchor'] else metrics['gap']
        if any(box_distance(box, b) < required_gap - EPS for b in old_boxes) or any(
                box_distance(box, boxes[j]) < (metrics['anchorMinGap'] if lots[j]['facesAnchor'] else required_gap) - EPS for j in range(i)):
            errors.append(ident + ': measured building gap violated')
        for other in data['existing'] + lots[:i]:
            if lot['sourceBuildingId'] == other['sourceBuildingId'] and box_distance(box, footprint(other)) < 12:
                errors.append(ident + ': identical source adjacent')
        if any(point_box_distance((p['x'], p['z']), box) < POST_RADIUS for p in data['posts'] + lamps):
            errors.append(ident + ': lamp/signal overlap')
        for a in data['anchors']:
            p, q = (a['position'][0], a['position'][2]), (a['roadPoint'][0], a['roadPoint'][2])
            if segment_box_distance(p, q, box) < SIDEWALK / 2 - EPS:
                errors.append(ident + ': anchor access blocked ' + a['name'])
        if lot['facesAnchor'] in moved:
            a = moved[lot['facesAnchor']]
            fx, fz = FRONTS[lot['rotationDeg']]
            px, pz = a['position'][0] - lot['x'], a['position'][2] - lot['z']
            gap = px * fx + pz * fz - lot['depth'] / 2
            lateral = abs(-px * fz + pz * fx)
            toward = fx * a['forward'][0] + fz * a['forward'][2]
            if lot['depth'] < ANCHOR_DEPTH - EPS or not .5 <= gap <= 9.1 or lateral > lot['width'] / 2 - .49 or toward < .98 or lot['zone'] != a['zone']:
                errors.append(ident + ': anchor is not in front of an 8m-deep facade')
        path = access_by_id.get(ident)
        if path:
            expected = access_for(lot, roads[path['road']])
            if path != expected:
                errors.append(ident + ': invalid frontage access path')
            for j, other in enumerate(old_boxes + boxes):
                if j == len(old_boxes) + i:
                    continue
                if segment_box_distance(path['start'], path['end'], other) < SIDEWALK / 2 - EPS:
                    errors.append(ident + ': 2m frontage access blocked')
    if len(lamps) != metrics['lampTarget']:
        errors.append('Lamp density target mismatch')
    all_post_points = [(p['x'], p['z']) for p in data['posts']]
    for lamp in lamps:
        p = lamp['x'], lamp['z']
        box = p[0] - POST_RADIUS, p[1] - POST_RADIUS, p[0] + POST_RADIUS, p[1] + POST_RADIUS
        r = roads.get(lamp['road'])
        if r is None:
            errors.append('Lamp invalid road')
            continue
        if not any(box_in_zone(box, poly) for poly in polygons.values()):
            errors.append('Lamp border overlap')
        if any(segment_box_distance(s['a'], s['b'], box) < s['width'] / 2 + SIDEWALK - EPS for s in roads.values()):
            errors.append('Lamp blocks 2m sidewalk')
        if any(point_box_distance(p, b) < POST_RADIUS for b in old_boxes + boxes):
            errors.append('Lamp building overlap')
        if min(distance(p, q) for q in all_post_points) < 5 - EPS:
            errors.append('Lamp too close to another post')
        q = project(p, r['a'], r['b'])
        angle = lamp['rotationDeg']
        if not math.isfinite(angle) or not 0 <= angle < 360:
            errors.append('Lamp invalid heading')
        else:
            facing = math.sin(math.radians(angle)), -math.cos(math.radians(angle))
            if facing[0] * (q[0] - p[0]) + facing[1] * (q[1] - p[1]) < distance(p, q) * .999:
                errors.append('Lamp faces away from road')
        if abs(distance(p, q) - (r['width'] / 2 + SIDEWALK + POST_RADIUS + .1)) > .001:
            errors.append('Lamp outside assigned road furnishing line')
        if any(s is not r and point_segment_distance(q, s['a'], s['b']) < s['width'] / 2 + 5 - .001 for s in roads.values()):
            errors.append('Lamp invades junction/signal/crosswalk reserve')
        if any(abs(p[0] - x) < 4.35 and abs(p[1]) < 8 for x in (8, 36, 101)):
            errors.append('Lamp invades existing crossing')
        if any(point_segment_distance(p, a['start'], a['end']) < SIDEWALK / 2 + POST_RADIUS - EPS for a in access):
            errors.append('Lamp blocks frontage access')
        for a in data['anchors']:
            start, end = (a['position'][0], a['position'][2]), (a['roadPoint'][0], a['roadPoint'][2])
            if point_segment_distance(p, start, end) < SIDEWALK / 2 + POST_RADIUS - EPS:
                errors.append('Lamp blocks anchor access')
        all_post_points.append(p)
    if errors:
        raise ValueError('\n'.join(errors[:30]) + f'\nTotal violations: {len(errors)}')
    # Baseline is recorded separately: do not claim old narrow sidewalks have been rebuilt.
    baseline = []
    for old, box in zip(data['existing'], old_boxes):
        narrow = [r['id'] for r in roads.values() if segment_box_distance(r['a'], r['b'], box) < r['width'] / 2 + SIDEWALK - EPS]
        if narrow:
            baseline.append(dict(id=old['id'], roads=narrow))
    return dict(passed=True, newBuildingCount=len(lots), retainedBuildingCount=len(old_boxes),
                totalBuildingCount=len(lots) + len(old_boxes), newLampCount=len(lamps),
                newOverlapViolations=0, movedAnchorFrontages=f'{len(moved)}/{len(moved)}',
                zoneContainment=f'{len(lots)}/{len(lots)}', validSourceIds=f'{len(lots)}/{len(lots)}',
                sourceUseMin=min(usage.values()), sourceUseMax=max(usage.values()),
                baselineNarrowSidewalks=baseline, playTest=False)


def picture(path, data, metrics, lots, lamps, validation):
    from PIL import Image, ImageDraw, ImageFont
    font_path = next(p for p in (Path('/mnt/c/Windows/Fonts/malgun.ttf'), Path('C:/Windows/Fonts/malgun.ttf')) if p.exists())
    font = lambda size: ImageFont.truetype(str(font_path), size)
    px, margin, panel = 7, 40, 560
    minimum, size = data['zones']['bounds']['minimum'], data['zones']['bounds']['size']
    width, height = round(size[0] * px) + margin * 2 + panel, round(size[1] * px) + margin * 2
    im = Image.new('RGB', (width, height), '#f4f3ef')
    draw = ImageDraw.Draw(im, 'RGBA')
    def xy(x, z):
        return margin + (x - minimum[0]) * px, margin + (minimum[1] + size[1] - z) * px
    colors = dict(A='#efd9c2', B='#cfdeec', C='#d3e6cd', D='#e3d3e6')
    for z in data['zones']['zones']:
        draw.polygon([xy(*p) for p in z['polygon']], fill=colors[z['id']])
    # Draw every sidewalk before any carriageway, so later curb caps cannot
    # paint false white gaps across an earlier road at a connected junction.
    for extra in (2 * SIDEWALK, 0):
        for i, r in enumerate(data['roads']):
            a, b = xy(*r['a']), xy(*r['b'])
            color = '#fcfbf8' if extra else '#4d5560' if i < len(data['original']['roads']) else '#e8853a'
            w = round((r['width'] + extra) * px)
            draw.line([a, b], fill=color, width=w)
            for p in (a, b):
                draw.ellipse((p[0] - w / 2, p[1] - w / 2, p[0] + w / 2, p[1] + w / 2), fill=color)
    for lot in data['existing'] + lots:
        box = footprint(lot)
        old = lot in data['existing']
        color = '#8c8f94' if old else '#386b60' if lot['facesAnchor'] else '#f6f0e3'
        draw.rectangle([xy(box[0], box[3]), xy(box[2], box[1])], fill=color, outline='#4b5353', width=2)
        if not old:
            fx, fz = FRONTS[lot['rotationDeg']]
            front = (lot['x'] + fx * lot['depth'] / 2, lot['z'] + fz * lot['depth'] / 2)
            draw.line([xy(*front), xy(front[0] + fx * 1.2, front[1] + fz * 1.2)], fill='#185e4d', width=3)
    for border in data['zones']['borders']:
        draw.line([xy(*border['from']), xy(*border['to'])], fill='#1b1b1b', width=4)
    outline = [(-10, 9), (125, 9), (125, -110), (-10, -110)]
    for a, b in zip(outline, outline[1:] + outline[:1]):
        a, b = xy(*a), xy(*b)
        n = distance(a, b)
        for s in range(0, round(n), 22):
            draw.line([(a[0] + (b[0] - a[0]) * s / n, a[1] + (b[1] - a[1]) * s / n),
                       (a[0] + (b[0] - a[0]) * min(n, s + 12) / n, a[1] + (b[1] - a[1]) * min(n, s + 12) / n)], fill='#20242a', width=3)
    for lamp in lamps:
        x, y = xy(lamp['x'], lamp['z'])
        draw.ellipse((x - 4, y - 4, x + 4, y + 4), fill='#eecc38', outline='#69520b', width=1)
    for a in data['anchors']:
        x, y = xy(a['position'][0], a['position'][2])
        if a['type'] == 'home':
            draw.polygon([(x, y - 15), (x - 14, y), (x - 9, y), (x - 9, y + 13), (x + 9, y + 13), (x + 9, y), (x + 14, y)], fill='#181818')
            continue
        moved = a.get('building') is None
        r = 8 if a['type'] == 'shop' else 6
        fill = '#ffffff' if moved else '#e7b900' if a['type'] == 'shop' else '#1f6fc4'
        shape = draw.rectangle if a['type'] == 'shop' else draw.ellipse
        shape((x - r, y - r, x + r, y + r), fill=fill, outline='#14426b', width=2)
    for b in data['zones']['barriers']:
        x, y = xy(*b['position'])
        draw.rectangle((x - 6, y - 6, x + 6, y + 6), fill='#d22b2b', outline='#ffffff', width=1)
    left = margin + size[0] * px + 36
    y = 40
    def line(text, size=24, space=39):
        nonlocal y
        draw.text((left, y), text, font=font(size), fill='#20242a')
        y += space
    line('넓힌 맵 · 건물/가로등 배치', 32, 58)
    line('전체 290×235m / 원래 맵은 점선', 22, 53)
    line('회색: 기존 건물 52채')
    line('흰색: 추가 건물 / 초록: 지점 정면')
    line('주황: 새 도로 / 흰 띠: 인도 2m')
    line('노랑 점: 추가 가로등 / 검정: 구역 경계')
    line('빨강: 구역 막이 / 초록 선: 건물 정면', 21, 65)
    line('구역   넓힌 면적㎡   기존 / 추가 / 합계', 22, 43)
    old_counts = Counter(l['zone'] for l in data['existing'])
    for z in data['zones']['zones']:
        k = z['id']; n = metrics['targets'][k]
        line(f"{k}       {z['area']:>6,}          {old_counts[k]} / {n} / {old_counts[k] + n}")
    y += 32
    line(f"건물 추가 {len(lots)}채 · 전체 {len(lots) + 52}채", 26, 49)
    line(f"기존 밀도 {metrics['densityPer1000']:.3f}채 / 1,000㎡")
    line(f"가로등 추가 {len(lamps)}개 · 기존 {metrics['oldLampCount']}개", 25, 51)
    line(f"지점 28/28 · 추가 배치 충돌 0", 25, 50)
    line(f"복제 52종 · 사용 {validation['sourceUseMin']}~{validation['sourceUseMax']}회")
    line(f"건물 간격 {metrics['gap']:.2f}m (기존 중앙값)")
    line(f"인도 통행폭 {SIDEWALK:.1f}m · 경계 이격 {BORDER_GAP}m")
    line('Unity 씬 적용과 플레이 확인은 맵 담당', 21, 55)
    y += 16
    if validation['baselineNarrowSidewalks']:
        line('기존 좁은 인도는 원본 유지', 21, 39)
        line(f"기존 2m 미만 인도 인접 건물 {len(validation['baselineNarrowSidewalks'])}채", 21)
    else:
        line('기존 건물·인도 침범 0건', 21, 39)
    # Small unobtrusive zone labels in panel/edge instead of hiding buildings.
    for zone in data['zones']['zones']:
        poly = zone['polygon']
        x, y0 = xy(poly[0][0] + 2, poly[0][1] - 3)
        draw.text((x, y0), zone['id'], font=font(26), fill='#141414', stroke_width=2, stroke_fill='#ffffff')
    a, b = xy(-84, -147), xy(-34, -147)
    draw.line([a, b], fill='#222222', width=5)
    draw.text((a[0], a[1] - 28), '0              25             50m', font=font(19), fill='#222222')
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    im.save(path)


def input_hashes():
    return {name: file_hash(DATA / name) for name in ('layout.json', 'layout_expanded.json', 'zones.json', 'anchors_expanded.json')}


class AnchorUpdate:
    """Moves the fewest saved buildings and lamps needed for the current anchors."""

    def __init__(self, data, metrics, lots_doc, lamps_doc):
        self.data, self.metrics = data, metrics
        self.lots = copy.deepcopy(lots_doc['lots'])
        self.access = {a['lotId']: a for a in copy.deepcopy(lots_doc['accessPaths'])}
        self.lamps = copy.deepcopy(lamps_doc['lamps'])
        self.roads = {r['id']: r for r in data['roads']}
        self.anchors = {a['name']: a for a in data['anchors']}
        self.moved = {n: a for n, a in self.anchors.items() if a.get('building') is None and a['type'] in ('shop', 'door')}
        self.log = []

    def faced(self):
        return {lot['facesAnchor'] for lot in self.lots if lot['facesAnchor']}

    def planner(self, without=()):
        planner = Planner(self.data, self.metrics)
        planner.lots = [lot for lot in self.lots if lot['id'] not in without]
        planner.boxes = [footprint(lot) for lot in planner.lots]
        planner.access = [self.access[lot['id']] for lot in planner.lots]
        return planner

    @staticmethod
    def stands_before(lot, a):
        """The same frontage rule verify() enforces."""
        fx, fz = FRONTS[lot['rotationDeg']]
        px, pz = a['position'][0] - lot['x'], a['position'][2] - lot['z']
        gap = px * fx + pz * fz - lot['depth'] / 2
        lateral = abs(-px * fz + pz * fx)
        toward = fx * a['forward'][0] + fz * a['forward'][2]
        return lot['depth'] >= ANCHOR_DEPTH - EPS and .5 <= gap <= 9.1 and lateral <= lot['width'] / 2 - .49 and toward >= .98 and lot['zone'] == a['zone']

    def release_stale_facades(self):
        for lot in self.lots:
            name = lot['facesAnchor']
            if name and (name not in self.moved or not self.stands_before(lot, self.moved[name])):
                self.log.append(f"{lot['id']} no longer fronts {name}")
                lot['facesAnchor'] = None

    def place_facade(self, a):
        """Prefers a building already there, then the nearest deep building that fits, then one that fits once up to two
        free neighbours step aside to the nearest legal plots."""
        road = self.roads[a['road']]
        for lot in self.lots:
            if not lot['facesAnchor'] and self.stands_before(lot, a):
                lot['facesAnchor'] = a['name']
                self.log.append(f"{lot['id']} already stands before {a['name']}")
                return True
        options = self.facade_options(a)
        for lot, moved in options:
            if self.planner(without={lot['id']}).fits(moved, road, True):
                self.replace(lot, moved, road, f"for {a['name']}")
                return True
        if self.swap_source_for(a, road):
            return True
        tries = []
        for lot, moved in options:
            box = footprint(moved)
            neighbours = [o for o in self.lots if o['id'] != lot['id'] and box_distance(box, footprint(o)) < self.metrics['anchorMinGap'] - EPS]
            if not 0 < len(neighbours) <= 2 or any(o['facesAnchor'] for o in neighbours):
                continue
            without = {lot['id']} | {o['id'] for o in neighbours}
            if self.planner(without=without).fits(moved, road, True):
                tries.append((len(neighbours), distance((lot['x'], lot['z']), (moved['x'], moved['z'])), lot, moved, neighbours))
        tries.sort(key=lambda t: t[:2])
        for _, _, lot, moved, neighbours in tries:
            kept = copy.deepcopy((self.lots, self.access, self.log))
            self.replace(lot, moved, road, f"for {a['name']}")
            if all(self.rehome(next(o for o in self.lots if o['id'] == n['id']), f"to make room for {a['name']}") for n in neighbours):
                return True
            self.lots, self.access, self.log = kept
        return False

    def swap_source_for(self, a, road):
        """The zone has no free deep building here: a nearby free one takes a deep model from a free building of another
        zone, which takes its model in exchange. Every model stays in use the same number of times."""
        p = a['position']
        rot = heading((a['forward'][0], a['forward'][2]))
        fx, fz = FRONTS[rot]
        local = sorted((l for l in self.lots if not l['facesAnchor'] and l['zone'] == a['zone']), key=lambda l: distance((l['x'], l['z']), (p[0], p[2])))
        donors = [l for l in self.lots if not l['facesAnchor'] and l['zone'] != a['zone'] and l['depth'] >= ANCHOR_DEPTH - EPS]
        for near in local:
            for donor in donors:
                for gap in (.6, .9, 1.2, 1.6, 2.0, 2.5, 3.5, 4.5, 6.0):
                    for lateral in (0, -.5, .5, -1, 1, -2, 2):
                        if abs(lateral) > donor['width'] / 2 - .5:
                            continue
                        new_near = dict(near, sourceBuildingId=donor['sourceBuildingId'], width=donor['width'], depth=donor['depth'], rotationDeg=rot, facesAnchor=a['name'],
                                        x=round(p[0] - fx * (donor['depth'] / 2 + gap) - fz * lateral, 4), z=round(p[2] - fz * (donor['depth'] / 2 + gap) + fx * lateral, 4))
                        new_donor = dict(donor, sourceBuildingId=near['sourceBuildingId'], width=near['width'], depth=near['depth'])
                        planner = self.planner(without={near['id'], donor['id']})
                        if not planner.fits(new_near, road, True):
                            continue
                        donor_road = self.roads[self.access[donor['id']]['road']]
                        planner.add(new_near, road)
                        if not planner.fits(new_donor, donor_road):
                            continue
                        self.replace(near, new_near, road, f"for {a['name']}, model {donor['sourceBuildingId']} from {donor['id']}")
                        self.replace(donor, new_donor, donor_road, f"model {near['sourceBuildingId']} from {near['id']}")
                        return True
        return False

    def facade_options(self, a):
        p = a['position']
        rot = heading((a['forward'][0], a['forward'][2]))
        fx, fz = FRONTS[rot]
        free = [lot for lot in self.lots if not lot['facesAnchor'] and lot['zone'] == a['zone'] and lot['depth'] >= ANCHOR_DEPTH - EPS]
        free.sort(key=lambda lot: distance((lot['x'], lot['z']), (p[0], p[2])))
        options = []
        for lot in free:
            for gap in (.6, .9, 1.2, 1.6, 2.0, 2.5, 3.5, 4.5, 6.0, 7.5, 9.0):
                for lateral in (0, -.5, .5, -1, 1, -2, 2, -(lot['width'] / 2 - .5), lot['width'] / 2 - .5):
                    if abs(lateral) <= lot['width'] / 2 - .5:
                        options.append((lot, dict(lot, x=round(p[0] - fx * (lot['depth'] / 2 + gap) - fz * lateral, 4), z=round(p[2] - fz * (lot['depth'] / 2 + gap) + fx * lateral, 4),
                                                  rotationDeg=rot, facesAnchor=a['name'])))
        return options

    def replace(self, old, new, road, why):
        self.lots[self.lots.index(old)] = new
        self.access[new['id']] = access_for(new, road)
        self.log.append(f"{new['id']} {new['sourceBuildingId']} ({old['x']},{old['z']},{old['rotationDeg']}) -> ({new['x']},{new['z']},{new['rotationDeg']}) {why}")

    def rehome(self, lot, why):
        """The nearest legal plot of the building's zone, on a road or deeper in the block."""
        planner = self.planner(without={lot['id']})
        best = None
        for road in self.data['roads']:
            for along in range(6, int(road['length']) - 5, 2):
                q = (road['a'][0] + road['unit'][0] * along, road['a'][1] + road['unit'][1] * along)
                for side in (-1, 1):
                    nx, nz = -road['unit'][1] * side, road['unit'][0] * side
                    if max(abs(nx), abs(nz)) < .98:
                        continue
                    rot = heading((-nx, -nz))
                    for deeper in range(0, 22, 2):
                        offset = road['width'] / 2 + self.metrics['setback'] + .02 + deeper + lot['depth'] / 2
                        moved = dict(lot, x=round(q[0] + nx * offset, 4), z=round(q[1] + nz * offset, 4), rotationDeg=rot)
                        far = distance((lot['x'], lot['z']), (moved['x'], moved['z']))
                        if (best is None or far < best[0]) and planner.fits(moved, road):
                            best = (far, moved, road)
        if best is None:
            return False
        self.replace(lot, best[1], best[2], why)
        return True

    def clear_anchor_paths(self):
        for a in self.data['anchors']:
            start, end = (a['position'][0], a['position'][2]), (a['roadPoint'][0], a['roadPoint'][2])
            for lot in list(self.lots):
                if segment_box_distance(start, end, footprint(lot)) < SIDEWALK / 2 - EPS and not self.rehome(lot, f"off the way to {a['name']}"):
                    raise RuntimeError(f"Could not move {lot['id']} off {a['name']}")

    def clear_lanes(self):
        """Buildings standing on an original walking lane step off it the shortest way, or find another plot."""
        for lot in list(self.lots):
            if not blocks_lane(footprint(lot), self.data):
                continue
            planner = self.planner(without={lot['id']})
            road = self.roads[self.access[lot['id']]['road']]
            anchored = bool(lot['facesAnchor'])
            steps = sorted(((step * dx, step * dz) for step in (.25, .5, .75, 1, 1.5, 2, 2.5, 3, 4, 5) for dx, dz in FRONTS.values()), key=lambda v: abs(v[0]) + abs(v[1]))
            for dx, dz in steps:
                moved = dict(lot, x=round(lot['x'] + dx, 4), z=round(lot['z'] + dz, 4))
                if planner.fits(moved, road, anchored) and (not anchored or self.stands_before(moved, self.anchors[lot['facesAnchor']])):
                    self.replace(lot, moved, road, 'off a walking lane')
                    break
            else:
                if anchored or not self.rehome(lot, 'off a walking lane'):
                    raise RuntimeError('Cannot clear the lane under ' + lot['id'])

    def settle(self):
        """Buildings left too close to a neighbour, or with their way to the road cut, by what moved."""
        for lot in [l for l in self.lots if l['facesAnchor']]:
            path = self.access[lot['id']]
            for other in list(self.lots):
                if other['id'] != lot['id'] and segment_box_distance(path['start'], path['end'], footprint(other)) < SIDEWALK / 2 - EPS:
                    if other['facesAnchor'] or not self.rehome(other, f"off the way to {lot['id']}"):
                        raise RuntimeError(f"{other['id']} blocks {lot['id']}")
        for lot in list(self.lots):
            if lot['facesAnchor']:
                continue
            current = next(l for l in self.lots if l['id'] == lot['id'])
            box = footprint(current)
            others = [footprint(l) for l in self.lots if l['id'] != lot['id']] + [footprint(l) for l in self.data['existing']]
            close = any(box_distance(box, footprint(l)) < self.metrics['gap'] - EPS for l in self.lots if l['id'] != lot['id'] and not l['facesAnchor'])
            old_close = any(box_distance(box, footprint(l)) < self.metrics['gap'] - EPS for l in self.data['existing'])
            facade_close = any(box_distance(box, footprint(l)) < self.metrics['anchorMinGap'] - EPS for l in self.lots if l['facesAnchor'])
            path = self.access[lot['id']]
            cut = any(segment_box_distance(path['start'], path['end'], other) < SIDEWALK / 2 - EPS for other in others)
            if (close or old_close or facade_close or cut) and not self.rehome(current, 'left too close by the moves'):
                raise RuntimeError('No plot left for ' + lot['id'])

    def fix_lamps(self):
        """Saved lamps stay put unless a building or an anchor line now takes their spot."""
        lots = self.lots
        boxes = [footprint(lot) for lot in self.data['existing'] + lots]
        access = list(self.access.values())
        anchor_lines = [((a['position'][0], a['position'][2]), (a['roadPoint'][0], a['roadPoint'][2])) for a in self.data['anchors']]
        def blocked(p):
            return (any(point_box_distance(p, b) < POST_RADIUS + .15 for b in boxes)
                    or any(point_segment_distance(p, a, b) < SIDEWALK / 2 + POST_RADIUS + .2 for a, b in anchor_lines)
                    or any(point_segment_distance(p, a['start'], a['end']) < SIDEWALK / 2 + POST_RADIUS + .2 for a in access))
        posts = [(q['x'], q['z']) for q in self.data['posts']]
        for i, lamp in enumerate(self.lamps):
            here = lamp['x'], lamp['z']
            others = posts + [(l['x'], l['z']) for j, l in enumerate(self.lamps) if j != i]
            if not blocked(here) and min(distance(here, q) for q in others) >= 5 - EPS:
                continue
            spots = [c for c in lamp_sites(self.data, lots, list(self.access.values())) if min(distance((c['x'], c['z']), q) for q in others) >= 5]
            if not spots:
                raise RuntimeError('No legal lamp site near ' + str(here))
            spot = min(spots, key=lambda c: distance((c['x'], c['z']), here))
            self.log.append(f"lamp {i + 1} {here} -> ({spot['x']},{spot['z']})")
            self.lamps[i] = spot

    def run(self):
        self.release_stale_facades()
        self.clear_anchor_paths()
        for name, a in self.moved.items():
            if name in self.faced():
                continue
            if not self.place_facade(a):
                raise RuntimeError('No facade fits before ' + name)
        self.clear_lanes()
        self.settle()
        self.fix_lamps()
        return self.lots, [self.access[lot['id']] for lot in self.lots], self.lamps


def update_anchors(data):
    lots_doc, lamps_doc = read_json(DATA / 'lots_expanded.json'), read_json(DATA / 'furniture_expanded.json')
    metrics = measure(data)
    metrics['targets'] = lots_doc['metrics']['targets']
    update = AnchorUpdate(data, metrics, lots_doc, lamps_doc)
    lots, access, lamps = update.run()
    validation = verify(data, metrics, lots, lamps, access)
    before = {lot['id']: lot for lot in lots_doc['lots']}
    changed = [lot['id'] for lot in lots if {k: lot[k] for k in ('x', 'z', 'rotationDeg')} != {k: before[lot['id']][k] for k in ('x', 'z', 'rotationDeg')}]
    print('\n'.join(update.log), flush=True)
    print(f"Buildings moved: {len(changed)} of {len(lots)}; lamps moved: {sum(a != b for a, b in zip(lamps, lamps_doc['lamps']))} of {len(lamps)}", flush=True)
    lots_doc.update(inputSha256=input_hashes(), metrics=metrics, lots=lots, accessPaths=access, validation=validation)
    lamps_doc['lamps'] = lamps
    return lots_doc, lamps_doc


def build(data):
    metrics = measure(data)
    print('measurements', json.dumps(metrics), flush=True)
    planner = Planner(data, metrics)
    planner.place_anchors()
    print('anchors placed', len(planner.lots), flush=True)
    planner.fill()
    lamps = place_lamps(data, metrics, planner.lots, planner.access)
    validation = verify(data, metrics, planner.lots, lamps, planner.access)
    usage = {s: planner.usage[s] for s in sorted(data['sources'])}
    hashes = input_hashes()
    lots_doc = dict(schemaVersion=1, coordinates='Unity world X/Z, metres', mode='additional-only',
                    sourceBuildingId='Original layout.json lot[0]; clone that exact building, scale 1',
                    rotationConvention='0=-Z, 90=+X, 180=+Z, 270=-X; absolute layout heading; rotate clone by target minus original heading',
                    inputSha256=hashes, metrics=metrics, lots=planner.lots, accessPaths=planner.access,
                    sourceUsage=usage, validation=validation)
    lamps_doc = dict(schemaVersion=1, coordinates='Unity world X/Z, metres', mode='additional-only',
                     rotationConvention=lots_doc['rotationConvention'],
                     postColliderSize=[.4, 3, .4], retainedLampCount=metrics['oldLampCount'], lamps=lamps)
    return lots_doc, lamps_doc


class GeometryTests(unittest.TestCase):
    def test_parallel_road_endpoint_and_tangent(self):
        self.assertAlmostEqual(segment_box_distance((0, 0), (10, 0), (11, 1, 12, 2)), math.sqrt(2))
        self.assertEqual(segment_box_distance((0, 0), (10, 0), (3, 2, 5, 4)), 2)
        self.assertTrue(overlap((0, 0, 2, 2), (1, 1, 3, 3)))
        self.assertFalse(overlap((0, 0, 2, 2), (2, 0, 4, 2)))

    def test_original_lot_conversion_preserves_coordinates(self):
        row = ['A01', 'style', 5, 7, 10, 8, 3, 180]
        zones = {'zones': [{'id': 'A', 'polygon': [(0, 0), (20, 0), (20, 20), (0, 20)]}]}
        lot = lot_from_row(row, zones)
        self.assertEqual((lot['x'], lot['z'], lot['zone']), (5, 7, 'A'))

    def test_diagonal_road_corner(self):
        if 'segment_box_distance' not in globals():
            self.fail('Exact road/rectangle clearance is not implemented')
        self.assertAlmostEqual(segment_box_distance((-5, -5), (5, 5), (0, 2, 1, 3)), 2 ** -.5)
        self.assertEqual(segment_box_distance((-5, -5), (5, 5), (0, 0, 1, 1)), 0)

    def test_cardinal_dimensions(self):
        if 'footprint' not in globals():
            self.fail('Cardinal footprint is not implemented')
        self.assertEqual(footprint(dict(x=0, z=0, width=10, depth=8, rotationDeg=90)), (-4, -5, 4, 5))

    def test_concave_border(self):
        if 'box_in_zone' not in globals():
            self.fail('Full zone footprint containment is not implemented')
        polygon = [(0, 0), (10, 0), (10, 10), (6, 10), (6, 4), (4, 4), (4, 10), (0, 10)]
        self.assertFalse(box_in_zone((2, 3, 8, 8), polygon, .01))
        self.assertTrue(box_in_zone((1, 1, 3, 3), polygon, .01))



class OutputTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.data = load_inputs()
        cls.lots_doc = read_json(DATA / 'lots_expanded.json')
        cls.lamps_doc = read_json(DATA / 'furniture_expanded.json')
        cls.metrics = measure(cls.data)

    def check_output(self, lots=None, lamps=None):
        return verify(self.data, self.metrics, self.lots_doc['lots'] if lots is None else lots,
                      self.lamps_doc['lamps'] if lamps is None else lamps, self.lots_doc['accessPaths'])

    def test_full_saved_contract(self):
        result = self.check_output()
        self.assertTrue(result['passed'])
        faced = sum(1 for lot in self.lots_doc['lots'] if lot['facesAnchor'])
        self.assertEqual((result['newBuildingCount'], result['newLampCount'], result['movedAnchorFrontages']), (169, 109, f'{faced}/{faced}'))
        self.assertEqual(Counter(lot['zone'] for lot in self.lots_doc['lots']), Counter(dict(A=45, B=45, C=39, D=40)))
        self.assertEqual((result['sourceUseMin'], result['sourceUseMax']), (3, 4))

    def test_duplicate_building_is_rejected(self):
        lots = copy.deepcopy(self.lots_doc['lots'])
        lots[1]['x'], lots[1]['z'] = lots[0]['x'], lots[0]['z']
        with self.assertRaises(ValueError):
            self.check_output(lots=lots)

    def test_bad_source_and_missing_frontage_are_rejected(self):
        for field, value in [('sourceBuildingId', 'missing'), ('facesAnchor', None), ('zone', 'missing')]:
            lots = copy.deepcopy(self.lots_doc['lots'])
            next(lot for lot in lots if lot['facesAnchor'])[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.check_output(lots=lots)

    def test_lamp_in_road_is_rejected(self):
        lamps = copy.deepcopy(self.lamps_doc['lamps'])
        road = self.data['roads'][18]
        lamps[0].update(x=road['a'][0], z=road['a'][1])
        with self.assertRaises(ValueError):
            self.check_output(lamps=lamps)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--self-test', action='store_true')
    parser.add_argument('--check-only', action='store_true', help='Validate saved JSONs without regenerating')
    parser.add_argument('--update-anchors', action='store_true', help='Move only what the current anchors need')
    parser.add_argument('--picture', type=Path)
    parser.add_argument('--report-json', type=Path)
    args = parser.parse_args()
    if args.self_test:
        suite = unittest.defaultTestLoader.loadTestsFromTestCase(GeometryTests)
        if (DATA / 'lots_expanded.json').exists() and (DATA / 'furniture_expanded.json').exists():
            suite.addTests(unittest.defaultTestLoader.loadTestsFromTestCase(OutputTests))
        return 0 if unittest.TextTestRunner(verbosity=2).run(suite).wasSuccessful() else 1
    data = load_inputs()
    if args.check_only:
        lots_doc, lamps_doc = read_json(DATA / 'lots_expanded.json'), read_json(DATA / 'furniture_expanded.json')
        metrics = measure(data)
        validation = verify(data, metrics, lots_doc['lots'], lamps_doc['lamps'], lots_doc['accessPaths'])
        for name, digest in lots_doc['inputSha256'].items():
            if file_hash(DATA / name) != digest:
                raise ValueError('Input changed since bake: ' + name)
        if lots_doc['metrics'] != metrics or lots_doc['validation'] != validation:
            raise ValueError('Saved measurement/validation metadata differs from fresh checks')
        if lots_doc['sourceUsage'] != dict(Counter(l['sourceBuildingId'] for l in lots_doc['lots'])):
            raise ValueError('Saved source use counts differ from actual output')
    else:
        lots_doc, lamps_doc = update_anchors(data) if args.update_anchors else build(data)
        validation = lots_doc['validation']
        for name, doc in [('lots_expanded.json', lots_doc), ('furniture_expanded.json', lamps_doc)]:
            (DATA / name).write_text(json.dumps(doc, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    if args.picture:
        picture(args.picture, data, lots_doc['metrics'], lots_doc['lots'], lamps_doc['lamps'], validation)
    if args.report_json:
        args.report_json.write_text(json.dumps(dict(metrics=lots_doc['metrics'], validation=validation,
                                                   sourceUsage=lots_doc['sourceUsage']), indent=2) + '\n', encoding='utf-8')
    print(json.dumps(validation, ensure_ascii=False, indent=2))
    return 0


if __name__ == '__main__':
    sys.exit(main())
