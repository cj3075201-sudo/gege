using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;

namespace DeliveryRider.Editor
{
    // Editor-only placement using team meshes/collider shapes and conservative protected-volume checks.
    public static class TeamMapPlacement
    {
        private sealed class Record{public Bounds box;public string reason;}
        private sealed class Protected
        {
            private const float Cell=4f;
            private readonly Dictionary<Vector2Int,List<int>> cells=new Dictionary<Vector2Int,List<int>>();
            private readonly List<Record> records=new List<Record>();private readonly HashSet<int> tested=new HashSet<int>();
            internal readonly Dictionary<string,int> counts=new Dictionary<string,int>();
            internal void Add(Bounds box,string reason)
            {
                int id=records.Count;records.Add(new Record{box=box,reason=reason});counts[reason]=counts.TryGetValue(reason,out int n)?n+1:1;
                for(int x=Mathf.FloorToInt(box.min.x/Cell);x<=Mathf.FloorToInt(box.max.x/Cell);x++)for(int z=Mathf.FloorToInt(box.min.z/Cell);z<=Mathf.FloorToInt(box.max.z/Cell);z++)
                {var key=new Vector2Int(x,z);if(!cells.TryGetValue(key,out var list))cells.Add(key,list=new List<int>());list.Add(id);}
            }
            internal string Hit(Bounds box)
            {
                tested.Clear();for(int x=Mathf.FloorToInt(box.min.x/Cell);x<=Mathf.FloorToInt(box.max.x/Cell);x++)for(int z=Mathf.FloorToInt(box.min.z/Cell);z<=Mathf.FloorToInt(box.max.z/Cell);z++)
                {
                    if(!cells.TryGetValue(new Vector2Int(x,z),out var list))continue;
                    foreach(int id in list)if(tested.Add(id)&&records[id].box.Intersects(box))return records[id].reason;
                }
                return null;
            }
        }
        private static T In<T>(Scene scene)where T:Component=>scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<T>(true)).Single();
        private static RoadNetwork RoadsFor(Scene scene)=>(RoadNetwork)new SerializedObject(In<RiderSafeRecovery>(scene)).FindProperty("network").objectReferenceValue;
        private static Protected MakeProtected(Scene scene)
        {
            var output=new Protected();var planner=In<HazardPlanner>(scene);var data=new SerializedObject(planner);var candidates=(HazardCandidates)data.FindProperty("candidates").objectReferenceValue;var settings=(HazardSpawnSettings)data.FindProperty("settings").objectReferenceValue;
            var templates=new Dictionary<string,Bounds>();
            foreach(var kind in settings.Kinds)
            {
                var root=PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(kind.prefab));
                try
                {
                    // The slot root can have no collider while its moving model has one.
                    // Match Instantiate(position, rotation): replace root pose, retain its scale and every child pose.
                    root.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);
                    bool found=false;var bounds=new Bounds(Vector3.up,new Vector3(1.1f,2,1.1f));
                    foreach(var c in root.GetComponentsInChildren<Collider>(true))
                    {
                        Bounds local;
                        if(c is BoxCollider box)local=new Bounds(box.center,box.size);
                        else if(c is SphereCollider sphere)local=new Bounds(sphere.center,Vector3.one*sphere.radius*2);
                        else if(c is CapsuleCollider capsule){var size=Vector3.one*capsule.radius*2;size[capsule.direction]=capsule.height;local=new Bounds(capsule.center,size);}
                        else if(c is MeshCollider mesh&&mesh.sharedMesh!=null)local=mesh.sharedMesh.bounds;
                        else throw new InvalidOperationException("Uncovered hazard collider: "+kind.type+" "+c.GetType().Name);
                        var world=new Bounds(c.transform.TransformPoint(local.center),Vector3.zero);
                        for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)for(int z=-1;z<=1;z+=2)world.Encapsulate(c.transform.TransformPoint(local.center+Vector3.Scale(local.extents,new Vector3(x,y,z))));
                        if(!found){bounds=world;found=true;}else bounds.Encapsulate(world);
                    }
                    // Keep the candidate's standing spot free too, including collider-free procedural hazards.
                    bounds.Encapsulate(new Bounds(Vector3.up,new Vector3(1.1f,2,1.1f)));templates[kind.type]=bounds;
                }
                finally{PrefabUtility.UnloadPrefabContents(root);}
            }
            foreach(var candidate in candidates.Candidates)
            {
                var local=templates.TryGetValue(candidate.type,out var b)?b:new Bounds(Vector3.up,new Vector3(1.1f,2,1.1f));
                output.Add(RotateBounds(local,candidate.position,candidate.rotation),"hazardCandidate");
            }
            var network=RoadsFor(scene);var shape=In<RiderMotor>(scene).GetComponent<CapsuleCollider>();
            foreach(var road in network.Roads)
            {
                int n=Mathf.Max(1,Mathf.CeilToInt(road.Container.CalculateLength()/4f));for(int i=0;i<=n;i++)
                {Vector3 p=road.Container.EvaluatePosition(i/(float)n);p=network.OnGround(p,0);output.Add(new Bounds(p+Vector3.up*shape.height*.5f,new Vector3(shape.radius*2,shape.height,shape.radius*2)),"recoveryCandidate");}
            }
            var pedestrians=In<PedestrianNetwork>(scene);for(int i=0;i<pedestrians.PathCount;i++){var path=pedestrians.Path(i);AddPath(output,path.points,path.halfWidth+.5f+.12f,"pedestrianPath");}
            var traffic=In<TrafficNetwork>(scene);for(int i=0;i<traffic.PathCount;i++)AddPath(output,traffic.Path(i).points,1.15f,"vehiclePath");
            foreach(var barrier in scene.GetRootGameObjects().Where(g=>g.name=="ZoneBarriers"||g.name=="MapBoundary").SelectMany(g=>g.GetComponentsInChildren<BoxCollider>(true)))
            {
                var scale=barrier.transform.lossyScale;scale=new Vector3(Mathf.Abs(scale.x),Mathf.Abs(scale.y),Mathf.Abs(scale.z));
                output.Add(RotateBounds(new Bounds(Vector3.Scale(barrier.center,scale),Vector3.Scale(barrier.size,scale)),barrier.transform.position,barrier.transform.rotation),"zoneOrBoundaryWall");
            }
            var anchors=JArray.Parse(File.ReadAllText("Docs/MapLayout/Myeongji/anchors_expanded.json"));
            foreach(var anchor in anchors)
            {
                var p=V(anchor["position"]);output.Add(new Bounds(new Vector3(p.x,1,p.z),new Vector3(2.3f,2,2.3f)),"deliveryEntrance");
                if(anchor["roadPoint"]!=null)AddPath(output,new[]{V(anchor["roadPoint"]),p},.65f,"entranceAccess");
            }
            TeamMapVerification.Write("protected_geometry.json",new{counts=output.counts,hazardTemplates=templates.ToDictionary(k=>k.Key,k=>new{center=A(k.Value.center),size=A(k.Value.size)}),method="Conservative AABBs of every prefab child collider plus standing capsule fallback, sampled vehicle/pedestrian corridors, exact recovery sampling rule, delivery/access keepouts"});
            return output;
        }
        private static void AddPath(Protected output,Vector3[] points,float radius,string reason)
        {
            for(int i=1;i<points.Length;i++)
            {
                float length=Vector3.Distance(points[i-1],points[i]);int n=Mathf.Max(1,Mathf.CeilToInt(length));
                for(int j=0;j<=n;j++){var p=Vector3.Lerp(points[i-1],points[i],j/(float)n);output.Add(new Bounds(new Vector3(p.x,p.y+1,p.z),new Vector3(radius*2,2,radius*2)),reason);}
            }
        }
        internal static Bounds RotateBounds(Bounds local,Vector3 position,Quaternion rotation)
        {
            var result=new Bounds(position+rotation*local.center,Vector3.zero);for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)for(int z=-1;z<=1;z+=2)result.Encapsulate(position+rotation*(local.center+Vector3.Scale(local.extents,new Vector3(x,y,z))));return result;
        }
        private static Vector3 V(JToken p)=>new Vector3((float)p[0],p.Count()>2?(float)p[1]:0,p.Count()>2?(float)p[2]:(float)p[1]);
        private static float[] A(Vector3 p)=>new[]{p.x,p.y,p.z};
        private static Bounds OriginalArea(GameObject referencePrefab)
        {
            var filter=referencePrefab.transform.Find("Ground").GetComponentInChildren<MeshFilter>();var mesh=filter.sharedMesh.bounds;return new Bounds(filter.transform.TransformPoint(mesh.center),Vector3.Scale(mesh.size,filter.transform.lossyScale));
        }
        private static string Kind(Transform t)=>t.GetComponent<MeshFilter>().sharedMesh.name.Replace("Greenery_","").Replace("Prop_","");
        public static void Build(Scene scene,GameObject map,GameObject expansion,GameObject referencePrefab,GameObject referenceMap)
        {
            var protectedSpace=MakeProtected(scene);var network=RoadsFor(scene);var roads=network.Roads;var ground=map.transform.Find("Ground").GetComponentInChildren<MeshCollider>();var originals=OriginalArea(referencePrefab);float originalArea=originals.size.x*originals.size.z;float newArea=ground.bounds.size.x*ground.bounds.size.z-originalArea;
            if(originalArea<=0||newArea<=0)throw new InvalidOperationException("Original/expanded ground area invalid");
            var greenery=map.transform.Find("StreetGreenery");var props=map.transform.Find("EntranceDetails");var oldAdded=expansion.transform.Find("StreetDetails");if(oldAdded!=null)UnityEngine.Object.DestroyImmediate(oldAdded.gameObject);var added=new GameObject("StreetDetails").transform;added.SetParent(expansion.transform,false);
            var sourceGreen=referenceMap.transform.Find("StreetGreenery").Cast<Transform>().ToArray();var sourceProps=referenceMap.transform.Find("EntranceDetails").Cast<Transform>().ToArray();var sourceCounts=sourceGreen.Concat(sourceProps).GroupBy(Kind).ToDictionary(g=>g.Key,g=>g.Count());
            var removed=new List<object>();var placed=new Dictionary<string,int>();var rejected=new Dictionary<string,int>();var limits=sourceCounts.ToDictionary(k=>k.Key,k=>Mathf.FloorToInt(k.Value*newArea/originalArea));
            var buildingBounds=TeamMapDetailInstaller.Buildings(map).Values.Select(r=>r.bounds).Concat(expansion.GetComponentsInChildren<MeshRenderer>(true).Where(r=>r.name.Contains("_Expanded_")).Select(r=>r.bounds)).ToArray();
            string Block(Bounds b)
            {
                string reason=protectedSpace.Hit(b);if(reason!=null)return reason;
                var center=b.center;float radius=new Vector2(b.extents.x,b.extents.z).magnitude;
                foreach(var road in roads)if(road.FootprintContains(center,radius)&&road.DistanceToCentre(center)<road.HalfWidth+road.SidewalkWidth+radius+.12f)return "roadOrSidewalk";
                return null;
            }
            foreach(var group in new[]{greenery,props})foreach(var t in group.Cast<Transform>().ToArray())
            {
                var col=t.GetComponent<BoxCollider>();string reason=col==null?"missingTeamCollider":Block(col.bounds);if(reason==null)continue;
                removed.Add(new{name=t.name,kind=Kind(t),position=A(t.position),reason});UnityEngine.Object.DestroyImmediate(t.gameObject);
            }
            Physics.SyncTransforms();
            string MaterialAt(RaycastHit hit)
            {
                int triangle=hit.triangleIndex;for(int i=0;i<ground.sharedMesh.subMeshCount;i++){int count=(int)ground.sharedMesh.GetIndexCount(i)/3;if(triangle<count)return ground.GetComponent<Renderer>().sharedMaterials[i].name;triangle-=count;}return "";
            }
            bool Surface(Vector3 p,float radius,bool plotOnly)
            {
                for(int i=0;i<5;i++)
                {
                    var q=p+(i==0?Vector3.zero:new Vector3(i%2==0?radius:-radius,0,i<3?radius:-radius));
                    if(!ground.Raycast(new Ray(q+Vector3.up*30,Vector3.down),out var hit,60)||Mathf.Abs(hit.point.y)>.04f)return false;
                    string mat=MaterialAt(hit);if(plotOnly?mat!="ST_Plot":mat!="ST_Plot"&&mat!="ST_Pavement")return false;
                }
                return true;
            }
            void Reject(string why){rejected[why]=rejected.TryGetValue(why,out int n)?n+1:1;}
            bool CanPlace(Transform prototype,Vector3 position,Quaternion rotation,bool tree)
            {
                var box=prototype.GetComponent<BoxCollider>();var b=RotateBounds(new Bounds(box.center,box.size),position,rotation);string reason=Block(b);if(reason!=null){Reject(reason);return false;}
                var check=tree?new Bounds(position+Vector3.up*2.6f,new Vector3(2.8f,5.2f,2.8f)):b;
                if(tree&&buildingBounds.Any(a=>a.Intersects(check))){Reject("buildingCanopy");return false;}
                if(Physics.OverlapBox(b.center,b.extents,Quaternion.identity,~0,QueryTriggerInteraction.Ignore).Any(c=>c!=ground)){Reject("solidOrOtherProp");return false;}
                return true;
            }
            void Place(Transform prototype,string kind,Vector3 position,Quaternion rotation,string label)
            {
                var go=UnityEngine.Object.Instantiate(prototype.gameObject,added,false);go.name=label;go.transform.SetPositionAndRotation(position,rotation);placed[kind]=placed.TryGetValue(kind,out int n)?n+1:1;Physics.SyncTransforms();
            }
            foreach(string kind in new[]{"Tree","ShrubBed"})
            {
                var prototype=sourceGreen.First(t=>Kind(t)==kind);var candidates=new List<Vector3>();
                for(float x=7+Mathf.Ceil((ground.bounds.min.x-7)/3.3f)*3.3f;x<=ground.bounds.max.x;x+=3.3f)for(float z=-94+Mathf.Ceil((ground.bounds.min.z+94)/3.1f)*3.1f;z<=ground.bounds.max.z;z+=3.1f)
                {
                    var p=new Vector3(x,0,z);if(x>=originals.min.x&&x<=originals.max.x&&z>=originals.min.z&&z<=originals.max.z)continue;if(!Surface(p,.7f,true))continue;candidates.Add(p);
                }
                var nearby=greenery.Cast<Transform>().Select(t=>t.position).Concat(added.Cast<Transform>().Select(t=>t.position)).ToList();
                foreach(var p in candidates.OrderBy(p=>Mathf.Repeat(p.x*13.37f+p.z*7.19f,97)))
                {
                    int n=placed.TryGetValue(kind,out int v)?v:0;if(n>=limits[kind])break;float spacing=kind=="Tree"?10:5;if(nearby.Any(q=>Vector3.Distance(p,q)<spacing))continue;
                    // Team greenery stays near pedestrian life, 2.2..6m from a path.
                    var pedestrians=In<PedestrianNetwork>(scene);float near=float.PositiveInfinity;
                    for(int i=0;i<pedestrians.PathCount;i++){var path=pedestrians.Path(i);for(int j=1;j<path.points.Length;j++)near=Mathf.Min(near,Distance(p,path.points[j-1],path.points[j]));}
                    if(near>6||near<2.2f||!CanPlace(prototype,p,Quaternion.identity,kind=="Tree"))continue;
                    Place(prototype,kind,p,Quaternion.identity,kind+"_Expanded_"+n.ToString("000"));nearby.Add(p);
                }
            }
            var expandedLots=(JArray)JObject.Parse(File.ReadAllText("Docs/MapLayout/Myeongji/lots_expanded.json"))["lots"];var renderers=expansion.GetComponentsInChildren<MeshRenderer>(true).Where(r=>expandedLots.Any(l=>(string)l["id"]==r.name)).ToDictionary(r=>r.name);
            foreach(string kind in new[]{"Planter","Mailbox","AirUnit","Bin"})
            {
                var prototype=sourceProps.FirstOrDefault(t=>Kind(t)==kind);if(prototype==null)continue;var mesh=prototype.GetComponent<MeshFilter>().sharedMesh;
                foreach(var lot in expandedLots)
                {
                    int n=placed.TryGetValue(kind,out int v)?v:0;if(n>=limits[kind])break;string id=(string)lot["id"];if(!renderers.TryGetValue(id,out var renderer))continue;
                    var collider=renderer.GetComponent<MeshCollider>();float angle=(float)lot["rotationDeg"];var front=angle==0?Vector3.back:angle==180?Vector3.forward:angle==90?Vector3.right:Vector3.left;var side=Mathf.Abs(front.z)>.5f?Vector3.right:Vector3.forward;
                    bool done=false;foreach(var normal in new[]{front,side,-side})
                    {
                        var lateral=Mathf.Abs(normal.z)>.5f?Vector3.right:Vector3.forward;var b=renderer.bounds;float edge=Vector3.Dot(b.center,normal)+Mathf.Abs(Vector3.Dot(b.extents,normal));float low=Vector3.Dot(b.center,lateral)-Mathf.Abs(Vector3.Dot(b.extents,lateral));float high=Vector3.Dot(b.center,lateral)+Mathf.Abs(Vector3.Dot(b.extents,lateral));
                        foreach(float offset in new[]{low+.68f,high-.68f,low+1.15f,high-1.15f,low+1.7f,high-1.7f})
                        {
                            var start=lateral*offset+normal*(edge+2)+Vector3.up*(kind=="Mailbox"?1.35f:.45f);if(!collider.Raycast(new Ray(start,-normal),out var hit,5))continue;
                            var p=new Vector3(hit.point.x,kind=="Mailbox"?1.1f:0,hit.point.z)+normal*(mesh.bounds.size.z/2+.06f);float radius=new Vector2(mesh.bounds.size.x,mesh.bounds.size.z).magnitude/2;
                            if(!Surface(p,Mathf.Min(radius,.28f),false)||!CanPlace(prototype,p,Quaternion.LookRotation(normal),false))continue;
                            Place(prototype,kind,p,Quaternion.LookRotation(normal),id+"_"+kind);done=true;break;
                        }
                        if(done)break;
                    }
                }
            }
            var overlap=new List<object>();foreach(var root in new[]{greenery,props,added})foreach(var t in root.Cast<Transform>())
            {var col=t.GetComponent<BoxCollider>();string reason=col==null?"missingTeamCollider":Block(col.bounds);if(reason!=null)overlap.Add(new{name=t.name,reason});}
            var totals=greenery.Cast<Transform>().Concat(props.Cast<Transform>()).Concat(added.Cast<Transform>()).GroupBy(Kind).ToDictionary(g=>g.Key,g=>g.Count());
            TeamMapVerification.Write("placement_report.json",new{originalArea,newArea,teamSourceCounts=sourceCounts,additionalLimits=limits,added=placed,totals,removed,rejected,protectedOverlapCount=overlap.Count,overlap,teamColliderShapesRetained=true});
            if(overlap.Count>0)throw new InvalidOperationException("Decorative protected-volume overlap remains: "+overlap.Count);
            foreach(var p in placed)if(p.Value>limits[p.Key])throw new InvalidOperationException("Team density exceeded: "+p.Key);
        }
        internal static Func<Bounds,string> CreateBlocker(Scene scene)
        {
            var space=MakeProtected(scene);var roads=RoadsFor(scene).Roads;
            return b=>
            {
                string reason=space.Hit(b);float radius=new Vector2(b.extents.x,b.extents.z).magnitude;
                if(reason==null&&roads.Any(r=>r.FootprintContains(b.center,radius)&&r.DistanceToCentre(b.center)<r.HalfWidth+r.SidewalkWidth+radius+.12f))reason="roadOrSidewalk";
                return reason;
            };
        }
        // Read-only check after reopening the saved scene, independent of the placement loop.
        public static void Audit(Scene scene,int expectedCount=220)
        {
            var space=MakeProtected(scene);var roads=RoadsFor(scene).Roads;
            var groups=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<Transform>(true)).Where(t=>t.name=="StreetGreenery"||t.name=="EntranceDetails"||t.name=="StreetDetails").ToArray();
            var failures=new List<string>();int count=0;
            foreach(var t in groups.SelectMany(g=>g.Cast<Transform>()))
            {
                count++;var col=t.GetComponent<BoxCollider>();var mesh=t.GetComponent<MeshFilter>()?.sharedMesh;
                if(col==null||mesh==null){failures.Add(t.name+": missing team geometry");continue;}
                var expected=Kind(t)=="Tree"?new Bounds(new Vector3(0,1.5f,0),new Vector3(1.16f,3,1.16f)):mesh.bounds;
                if(Vector3.Distance(col.center,expected.center)>.0001f||Vector3.Distance(col.size,expected.size)>.0001f||col.isTrigger||t.localScale!=Vector3.one)failures.Add(t.name+": collider differs from team");
                var b=col.bounds;string reason=space.Hit(b);float radius=new Vector2(b.extents.x,b.extents.z).magnitude;
                if(reason==null&&roads.Any(r=>r.FootprintContains(b.center,radius)&&r.DistanceToCentre(b.center)<r.HalfWidth+r.SidewalkWidth+radius+.12f))reason="roadOrSidewalk";
                if(reason!=null)failures.Add(t.name+": "+reason);
                if(!AssetDatabase.GetAssetPath(mesh).StartsWith(TeamMapDetailInstaller.Art+"/"))failures.Add(t.name+": non-team mesh");
            }
            TeamMapVerification.Write("saved_placement_audit.json",new{count,failures,colliderShapes="Tree: team1.16x3x1.16 at y1.5; other props: team mesh bounds; all scale1, solid",method="Saved scene reopened; full protected-volume and road/sidewalk checks repeated without changing placements"});
            if(count!=expectedCount||failures.Count>0)throw new InvalidOperationException("Saved art audit failed: "+count+" "+string.Join("; ",failures.Take(10)));
            Debug.Log("TEAM_MAP_SAVED_PLACEMENT_PASS count="+count+" overlap=0 colliderShapeDifferences=0");
        }
        private static float Distance(Vector3 p,Vector3 a,Vector3 b){p.y=a.y=b.y=0;var d=b-a;return Vector3.Distance(p,a+d*Mathf.Clamp01(Vector3.Dot(p-a,d)/Mathf.Max(.0001f,d.sqrMagnitude)));}
    }
}
