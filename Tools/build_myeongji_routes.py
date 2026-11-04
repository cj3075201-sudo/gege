"""Bake distances and a schematic map from the placed Myeongji anchors.

Default: the original 135 x 119 m map (layout.json, anchors.json).
--expanded: the widened map (layout_expanded.json, anchors_expanded.json, zones.json). Original inputs and outputs stay as they are.
--dry-run: compute and print the summary; write nothing.
"""
import argparse, json, math, heapq
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
root=Path(__file__).resolve().parents[1]
directory=root/'Docs/MapLayout/Myeongji'
FONTS=['C:/Windows/Fonts/arialbd.ttf','/mnt/c/Windows/Fonts/arialbd.ttf']
ZONE_COLORS={'A':'#c1a489','B':'#a4b5c4','C':'#a1b89e','D':'#b8a4b7'}
def read_json(name):return json.loads((directory/name).read_text(encoding='utf-8-sig'))
def font(size):
 for path in FONTS:
  if Path(path).exists():return ImageFont.truetype(path,size)
 raise RuntimeError('No map font found')
def dist(a,b):return math.hypot(a[0]-b[0],a[1]-b[1])
def project(p,r):
 a,b=r['a'],r['b'];dx=b[0]-a[0];dy=b[1]-a[1];t=max(0,min(1,((p[0]-a[0])*dx+(p[1]-a[1])*dy)/(dx*dx+dy*dy)))
 return (a[0]+dx*t,a[1]+dy*t)
def build_graph(roads,anchors):
 """Junction graph of road centrelines plus anchor access lines. Sets anchor['node']."""
 nodes=[];edges={};splits=[[] for _ in roads]
 def node(p):
  for i,q in enumerate(nodes):
   if dist(p,q)<.015:return i
  nodes.append(tuple(p));edges[len(nodes)-1]={};return len(nodes)-1
 def edge(i,j):
  if i==j:return
  w=dist(nodes[i],nodes[j]);edges[i][j]=min(edges[i].get(j,math.inf),w);edges[j][i]=min(edges[j].get(i,math.inf),w)
 def split(i,p):
  n=node(p);splits[i].append(n);return n
 for i,r in enumerate(roads):
  split(i,r['a']);split(i,r['b'])
 # Explicit near-end junctions bridge the small authored centreline gaps inside road widths.
 for i,r in enumerate(roads):
  for p in [r['a'],r['b']]:
   for j,s in enumerate(roads):
    if i==j:continue
    q=project(p,s)
    if dist(p,q)<=s['width']*.5+.05:edge(split(i,p),split(j,q))
 for i,r in enumerate(roads):
  for j,s in enumerate(roads[i+1:],i+1):
   a,b=r['a'],r['b'];c,d=s['a'],s['b'];u=(b[0]-a[0],b[1]-a[1]);v=(d[0]-c[0],d[1]-c[1]);den=u[0]*v[1]-u[1]*v[0]
   if abs(den)<1e-9:continue
   w=(c[0]-a[0],c[1]-a[1]);t=(w[0]*v[1]-w[1]*v[0])/den;q=(w[0]*u[1]-w[1]*u[0])/den
   if 0<=t<=1 and 0<=q<=1:
    p=(a[0]+t*u[0],a[1]+t*u[1]);split(i,p);split(j,p)
 for a in anchors:
  p=(a['position'][0],a['position'][2]);q=(a['roadPoint'][0],a['roadPoint'][2]);i=next(i for i,r in enumerate(roads) if r['id']==a['road'])
  a['node']=node(p);edge(a['node'],split(i,q))
 for i,r in enumerate(roads):
  ordered=sorted(set(splits[i]),key=lambda n:dist(nodes[n],r['a']))
  for a,b in zip(ordered,ordered[1:]):edge(a,b)
 return nodes,edges
def distances(edges,start):
 costs={start:0};queue=[(0,start)]
 while queue:
  cost,n=heapq.heappop(queue)
  if cost>costs[n]:continue
  for dest,w in edges[n].items():
   nc=cost+w
   if nc<costs.get(dest,math.inf):costs[dest]=nc;heapq.heappush(queue,(nc,dest))
 return costs
def route_data(anchors,nodes,edges,zone_names):
 shops=[a for a in anchors if a['type']=='shop'];doors=[a for a in anchors if a['type']=='door']
 allcosts={a['node']:distances(edges,a['node']) for a in anchors}
 if any(len(d)!=len(nodes) for d in allcosts.values()):raise RuntimeError('Disconnected road graph')
 def matrix(left,right):return [round(allcosts[a['node']][b['node']],2) for a in left for b in right]
 return {'zoneIds':list('ABCD'),'zoneNames':zone_names,'shopNames':[a['shopName'] for a in shops],'shopZones':[a['zone'] for a in shops],'doorZones':[a['zone'] for a in doors],'shopToDoor':matrix(shops,doors),'doorToDoor':matrix(doors,doors)}
def graph_data(anchors,nodes,edges):
 return {'nodes':nodes,'edges':[(a,b,w) for a,ee in edges.items() for b,w in ee.items() if a<b],'anchorNodes':{a['name']:a['node'] for a in anchors}}
def inside(polygon,x,z):
 hit=False;j=len(polygon)-1
 for i,(xi,zi) in enumerate(polygon):
  xj,zj=polygon[j]
  if (zi>z)!=(zj>z) and x<(xj-xi)*(z-zi)/(zj-zi)+xi:hit=not hit
  j=i
 return hit
def draw_map(layout,anchors,minimum,size,width,zone_of,labels):
 W=width;H=round(width*size[1]/size[0]);px=W/size[0]
 im=Image.new('RGB',(W,H),'#c4c7b5');draw=ImageDraw.Draw(im)
 def xy(x,z):return ((x-minimum[0])*px,(minimum[1]+size[1]-z)*px)
 for r in layout['roads']:
  a,b=xy(*r['a']),xy(*r['b']);road=round(r['width']*px)
  draw.line([a,b],fill='#728088',width=road)
  for x,y in [a,b]:draw.ellipse((x-road/2,y-road/2,x+road/2,y+road/2),fill='#728088')
 for lot in layout['lots']:
  ident,family,x,z,w,d,floors,angle=lot
  if angle%180:w,d=d,w
  draw.rectangle([xy(x-w/2,z+d/2),xy(x+w/2,z-d/2)],fill=ZONE_COLORS[zone_of(ident,x,z)],outline='#7d847e',width=2)
 label_font=font(round(width*30/1080))
 for label,x,z in labels:
  draw.text(xy(x,z),label,font=label_font,fill='#293b45',anchor='mm',stroke_width=2,stroke_fill='#edf0e6')
 home=next(a for a in anchors if a['type']=='home');x,y=xy(home['position'][0],home['position'][2]);draw.polygon([(x,y-9),(x-10,y),(x-7,y),(x-7,y+8),(x+7,y+8),(x+7,y),(x+10,y)],fill='#273c4a')
 return im
def original_zone(ident,x,z):return 'A' if ident.startswith('A') else 'B' if ident.startswith('B') else 'C' if ident[0] in 'EH' else 'D'
def main():
 parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--expanded',action='store_true');parser.add_argument('--dry-run',action='store_true');args=parser.parse_args()
 zone_names=['A 정문 앞','B 북쪽 상가','C 남쪽 주택','D 동쪽 골목']
 if args.expanded:
  layout=read_json('layout_expanded.json');anchors=read_json('anchors_expanded.json');zones=read_json('zones.json')
  # The map shows every building; the road graph does not depend on them.
  layout['lots']=layout['lots']+[[l['id'],l['sourceBuildingId'],l['x'],l['z'],l['width'],l['depth'],0,l['rotationDeg']] for l in read_json('lots_expanded.json')['lots']]
  polygons={z['id']:z['polygon'] for z in zones['zones']}
  def zone_of(ident,x,z):
   return next((k for k,p in polygons.items() if inside(p,x,z)),original_zone(ident,x,z))
  labels=[(z['id'],z['label'][0],z['label'][1]) for z in zones['zones']]
  minimum=zones['bounds']['minimum'];size=zones['bounds']['size'];width=2048
  table_path,graph_path,image_path=directory/'route_table_expanded.json',directory/'road_graph_expanded.json',directory/'MyeongjiMap_Expanded.png'
 else:
  layout=read_json('layout.json');anchors=read_json('anchors.json');zone_of=original_zone
  labels=[('A',13,-29),('B',72,-36),('C',32,-70),('D',95,-88)];minimum=[-10,-110];size=[135,119];width=1080
  table_path,graph_path,image_path=directory/'route_table.json',directory/'road_graph.json',root/'Assets/_Game/Art/Myeongji/Map/MyeongjiMap.png'
 nodes,edges=build_graph(layout['roads'],anchors)
 data=route_data(anchors,nodes,edges,zone_names)
 shops=[a for a in anchors if a['type']=='shop'];doors=[a for a in anchors if a['type']=='door']
 summary={'nodes':len(nodes),'edges':sum(map(len,edges.values()))//2,'shops':len(shops),'doors':len(doors),'routeRangeMeters':[min(data['shopToDoor']),max(data['shopToDoor'])],'connected':True}
 if not args.dry_run:
  table_path.write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf8')
  graph_path.write_text(json.dumps(graph_data(anchors,nodes,edges),indent=2),encoding='utf8')
  image_path.parent.mkdir(parents=True,exist_ok=True)
  draw_map(layout,anchors,minimum,size,width,zone_of,labels).save(image_path)
 print(json.dumps(summary))
 return data,graph_data(anchors,nodes,edges)
if __name__=='__main__':main()
