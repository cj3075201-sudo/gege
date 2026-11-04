"""Link an asset-exported hazard bake to delivery routes, without touching the asset.

Export with DeliveryRider.Editor.MyeongjiHazardExpansionBake.ExportSource/BakeRoads,
then pass --bake and a separate --output. Legacy graphs can be selected explicitly
for reproduction checks. Route IDs remain shop index * door count + door index.
"""
import argparse
import copy
import heapq
import json
import math
from pathlib import Path

BASE = Path(__file__).resolve().parents[1] / 'Docs/MapLayout/Myeongji'


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def distance(p, a, b):
    dx, dy = b[0] - a[0], b[1] - a[1]
    squared = dx * dx + dy * dy
    t = max(0, min(1, ((p[0]-a[0])*dx + (p[1]-a[1])*dy) / squared)) if squared else 0
    return math.hypot(p[0]-a[0]-t*dx, p[1]-a[1]-t*dy)


def link(bake, graph, anchors, route_table):
    nodes = graph['nodes']
    edges = {i: [] for i in range(len(nodes))}
    for a, b, cost in graph['edges']:
        if a not in edges or b not in edges or not math.isfinite(cost) or cost < 0:
            raise ValueError('Invalid graph edge')
        edges[a].append((b, cost))
        edges[b].append((a, cost))

    def tree(start):
        costs, previous, queue = {start: 0}, {}, [(0, start)]
        while queue:
            cost, node = heapq.heappop(queue)
            if cost > costs[node]:
                continue
            for dest, weight in edges[node]:
                if cost + weight < costs.get(dest, math.inf):
                    costs[dest] = cost + weight
                    previous[dest] = node
                    heapq.heappush(queue, (cost + weight, dest))
        return previous

    # Preserve the original array order. Reject a shuffled indexed array instead
    # of silently changing the runtime route IDs.
    shops = [a for a in anchors if a['type'] == 'shop']
    doors = [a for a in anchors if a['type'] == 'door']
    for kind, values in [('shop', shops), ('door', doors)]:
        if not values:
            raise ValueError('Missing ' + kind + ' anchors')
        for i, anchor in enumerate(values):
            if anchor.get('index', i) != i:
                raise ValueError('Anchor index/order mismatch: ' + anchor['name'])
            if graph['anchorNodes'].get(anchor['name']) not in edges:
                raise ValueError('Missing graph anchor: ' + anchor['name'])
    total = len(shops) * len(doors)
    if len(route_table['shopToDoor']) != total or len(route_table['shopNames']) != len(shops):
        raise ValueError('Route table dimensions do not match anchors')
    if route_table['shopZones'] != [a['zone'] for a in shops] or route_table['doorZones'] != [a['zone'] for a in doors]:
        raise ValueError('Route table zone/order mismatch')

    result = copy.deepcopy(bake)
    spots = result['spots']
    for c in result['candidates']:
        if c['spot'] < 0 or c['spot'] >= len(spots):
            raise ValueError('Candidate spot out of range')
    for spot in spots:
        p = spot['position']
        if not all(math.isfinite(p[k]) for k in ('x', 'y', 'z')):
            raise ValueError('Nonfinite spot position')
        spot['routes'] = []
    covered = []
    for si, shop in enumerate(shops):
        start = graph['anchorNodes'][shop['name']]
        previous = tree(start)
        for di, door in enumerate(doors):
            dest = graph['anchorNodes'][door['name']]
            path = [dest]
            while path[-1] != start:
                if path[-1] not in previous:
                    raise ValueError('Unreachable route: ' + shop['name'] + ' -> ' + door['name'])
                path.append(previous[path[-1]])
            segments = list(zip(path, path[1:]))
            route, hit = si * len(doors) + di, False
            for spot in spots:
                p = (spot['position']['x'], spot['position']['z'])
                if any(distance(p, nodes[a], nodes[b]) <= 7 for a, b in segments):
                    spot['routes'].append(route)
                    hit = True
            if hit:
                covered.append(route)
    missing = sorted(set(range(total)) - set(covered))
    summary = dict(spots=len(spots), candidates=len(result['candidates']),
                   coveredRoutes=len(covered), totalRoutes=total,
                   spotsWithoutRoutes=sum(not s['routes'] for s in spots), missingRoutes=missing)
    return result, summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bake', type=Path, required=True, help='JSON exported from the asset, never the stale hazard_candidates.json')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--graph', type=Path, default=BASE / 'road_graph_expanded.json')
    parser.add_argument('--anchors', type=Path, default=BASE / 'anchors_expanded.json')
    parser.add_argument('--route-table', type=Path, default=BASE / 'route_table_expanded.json')
    parser.add_argument('--summary', type=Path)
    parser.add_argument('--allow-uncovered', action='store_true', help='Diagnostic only; normally all routes must be covered')
    args = parser.parse_args()
    inputs = {p.resolve() for p in (args.bake, args.graph, args.anchors, args.route_table)}
    outputs = [args.output.resolve()] + ([args.summary.resolve()] if args.summary else [])
    if any(p in inputs for p in outputs) or len(outputs) != len(set(outputs)):
        parser.error('Input/output paths must be distinct')
    try:
        result, summary = link(read_json(args.bake), read_json(args.graph), read_json(args.anchors), read_json(args.route_table))
        if summary['missingRoutes'] and not args.allow_uncovered:
            raise ValueError('Routes without hazard spots: ' + str(summary['missingRoutes']))
    except (ValueError, KeyError, TypeError, IndexError) as error:
        parser.error(str(error))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    if args.summary:
        args.summary.parent.mkdir(parents=True, exist_ok=True)
        args.summary.write_text(json.dumps(summary, indent=2), encoding='utf-8')
    print(json.dumps(summary))


if __name__ == '__main__':
    main()
