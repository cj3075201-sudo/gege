using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeliveryRider.Editor
{
    // Restores removed team props locally; never rebuilds the expansion or changes gameplay data.
    public static class TeamMapOriginalRestoration
    {
        internal const string Reference="Assets/TeamOriginalDetails.prefab";
        internal static string FilePath(string name)=>Path.Combine(TeamMapVerification.Output,name);
        private static float[] A(Vector3 p)=>new[]{p.x,p.y,p.z};
        private static string Kind(Transform t)=>t.GetComponent<MeshFilter>().sharedMesh.name.Replace("Greenery_","").Replace("Prop_","");
        private static JObject Expansion()
        {
            var root=GameObject.Find("MyeongjiMapExpansion");var data=new JObject();
            foreach(var renderer in root.GetComponentsInChildren<MeshRenderer>(true))data[TeamMapVerification.PathOf(renderer.transform)]=new JObject{["mesh"]=AssetDatabase.GetAssetPath(renderer.GetComponent<MeshFilter>().sharedMesh),["materials"]=new JArray(renderer.sharedMaterials.Select(m=>AssetDatabase.GetAssetPath(m))),["enabled"]=renderer.enabled,["shadows"]=(int)renderer.shadowCastingMode};
            return data;
        }
        public static void Restore()
        {
            var scene=EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);Physics.SyncTransforms();TeamMapVerification.CompareProtected();
            if(!File.Exists(FilePath("followup_before.json")))TeamMapVerification.Write("followup_before.json",TeamMapVerification.Snapshot());
            if(!File.Exists(FilePath("followup_expansion_before.json")))TeamMapVerification.Write("followup_expansion_before.json",Expansion());
            var source=PrefabUtility.LoadPrefabContents(Reference);source.SetActive(false);SceneManager.SetActiveScene(scene);Physics.SyncTransforms();
            try
            {
                var map=GameObject.Find("MyeongjiMap");var expansion=GameObject.Find("MyeongjiMapExpansion");var green=map.transform.Find("StreetGreenery");var props=map.transform.Find("EntranceDetails");var ground=map.transform.Find("Ground").GetComponentInChildren<MeshCollider>();
                var blocker=TeamMapPlacement.CreateBlocker(scene);var buildings=TeamMapDetailInstaller.Buildings(map);var roads=((RoadNetwork)new SerializedObject(GameObject.Find("Player").GetComponent<RiderSafeRecovery>()).FindProperty("network").objectReferenceValue).Roads;
                var pedestrians=GameObject.Find("Pedestrians").GetComponent<PedestrianNetwork>();var segments=new List<(Vector3 a,Vector3 b)>();for(int i=0;i<pedestrians.PathCount;i++){var path=pedestrians.Path(i);for(int j=1;j<path.points.Length;j++)segments.Add((path.points[j-1],path.points[j]));}
                var previous=File.Exists(FilePath("restoration_report.json"))?JObject.Parse(File.ReadAllText(FilePath("restoration_report.json"))):null;
                var restored=previous==null?new List<object>():((JArray)previous["restored"]).Cast<object>().ToList();var omitted=new List<object>();var shots=File.Exists(FilePath("restoration_views.json"))?JArray.Parse(File.ReadAllText(FilePath("restoration_views.json"))).Cast<object>().ToList():new List<object>();
                float NearPath(Vector3 p)=>segments.Min(s=>Distance(p,s.a,s.b));
                RoadSurface Street(Vector3 p)=>roads.OrderBy(r=>r.DistanceToCentre(p)).First();
                bool Surface(Vector3 p,Bounds b,bool greenery)
                {
                    foreach(var offset in new[]{Vector3.zero,new Vector3(b.extents.x,0,b.extents.z),new Vector3(-b.extents.x,0,b.extents.z),new Vector3(b.extents.x,0,-b.extents.z),new Vector3(-b.extents.x,0,-b.extents.z)})
                    {
                        var q=p+offset;if(!ground.Raycast(new Ray(new Vector3(q.x,30,q.z),Vector3.down),out var hit,60)||Mathf.Abs(hit.point.y)>.04f)return false;
                        int tri=hit.triangleIndex;string material="";for(int i=0;i<ground.sharedMesh.subMeshCount;i++){int n=(int)ground.sharedMesh.GetIndexCount(i)/3;if(tri<n){material=ground.GetComponent<Renderer>().sharedMaterials[i].name;break;}tri-=n;}
                        if(greenery?material!="ST_Plot":material!="ST_Plot"&&material!="ST_Pavement")return false;
                    }
                    return true;
                }
                foreach(string groupName in new[]{"StreetGreenery","EntranceDetails"})foreach(var prototype in source.transform.Find(groupName).Cast<Transform>())
                {
                    var parent=groupName=="StreetGreenery"?green:props;if(parent.Find(prototype.name)!=null)continue;
                    var original=prototype.position;var kind=Kind(prototype);var box=prototype.GetComponent<BoxCollider>();var local=new Bounds(box.center,box.size);var candidates=new List<(Vector3 p,Quaternion q,Vector3 normal)>();var rejects=new Dictionary<string,int>();bool greenery=groupName=="StreetGreenery";
                    void Reject(string reason){rejects[reason]=rejects.TryGetValue(reason,out int n)?n+1:1;}
                    candidates.Add((original,prototype.rotation,prototype.forward));
                    if(greenery)
                    {
                        for(float x=-16;x<=16;x+=.25f)for(float z=-16;z<=16;z+=.25f)if(x*x+z*z<=256)candidates.Add((original+new Vector3(x,0,z),prototype.rotation,Vector3.back));
                    }
                    else
                    {
                        string id=prototype.name.Split('_')[0];var renderer=buildings[id];var b=renderer.bounds;var collider=renderer.GetComponent<MeshCollider>();
                        foreach(var normal in new[]{Vector3.forward,Vector3.back,Vector3.left,Vector3.right})
                        {
                            var lateral=Mathf.Abs(normal.z)>.5f?Vector3.right:Vector3.forward;float edge=Vector3.Dot(b.center,normal)+Mathf.Abs(Vector3.Dot(b.extents,normal));float low=Vector3.Dot(b.center,lateral)-Mathf.Abs(Vector3.Dot(b.extents,lateral));float high=Vector3.Dot(b.center,lateral)+Mathf.Abs(Vector3.Dot(b.extents,lateral));
                            for(float along=low+local.size.x*.5f+.12f;along<=high-local.size.x*.5f-.12f;along+=.15f)
                            {
                                var ray=lateral*along+normal*(edge+2)+Vector3.up*(kind=="Mailbox"?1.35f:.45f);
                                if(!collider.Raycast(new Ray(ray,-normal),out var hit,5))continue;
                                foreach(float gap in new[]{.08f,.18f})
                                {var p=new Vector3(hit.point.x,original.y,hit.point.z)+normal*(local.size.z*.5f+gap);candidates.Add((p,Quaternion.LookRotation(normal),normal));}
                            }
                        }
                    }
                    bool placed=false;var street=greenery?Street(original):null;
                    foreach(var candidate in candidates.OrderBy(c=>(c.p-original).sqrMagnitude))
                    {
                        var p=candidate.p;if(p.x< -10||p.x>125||p.z< -110||p.z>9){Reject("outsideOriginalArea");continue;}
                        var bounds=TeamMapPlacement.RotateBounds(local,p,candidate.q);string reason=blocker(bounds);if(reason!=null){Reject(reason);continue;}
                        if(!Surface(p,bounds,greenery)){Reject("surface");continue;}
                        if(greenery)
                        {
                            if(!street.FootprintContains(p,6)||street.DistanceToCentre(p)>street.HalfWidth+street.SidewalkWidth+6){Reject("differentStreet");continue;}
                            float near=NearPath(p);if(near<2.2f||near>6){Reject("teamPathDistance");continue;}
                            if(green.Cast<Transform>().Any(t=>Vector3.Distance(p,t.position)<(kind=="Tree"&&Kind(t)=="Tree"?10:5))){Reject("teamSpacing");continue;}
                            if(kind=="Tree"&&buildings.Values.Any(r=>r.bounds.Intersects(new Bounds(p+Vector3.up*2.6f,new Vector3(2.8f,5.2f,2.8f))))){Reject("buildingCanopy");continue;}
                        }
                        if(Physics.OverlapBox(bounds.center,bounds.extents,Quaternion.identity,~0,QueryTriggerInteraction.Ignore).Any(c=>c!=ground)){Reject("solidOrOtherProp");continue;}
                        var copy=UnityEngine.Object.Instantiate(prototype.gameObject,parent,false);copy.name=prototype.name;copy.transform.SetPositionAndRotation(p,candidate.q);Physics.SyncTransforms();
                        string association=greenery?"same street "+street.name:"same building "+prototype.name.Split('_')[0];
                        restored.Add(new{name=prototype.name,kind,from=A(original),to=A(p),distance=Vector3.Distance(original,p),association});
                        Vector3 outward=greenery?(original-street.transform.position).normalized:candidate.normal;outward.y=0;if(greenery||outward.sqrMagnitude<.5f)outward=Vector3.back;
                        var look=bounds.center;look.y=Mathf.Max(kind=="Tree"?1.8f:.65f,look.y);var eye=look+outward*(kind=="Tree"?6:4)+Vector3.up*(kind=="Tree"?1.4f:.9f);
                        shots.Add(new{label="restored_"+prototype.name,position=A(eye),look=A(look),objectPosition=A(p),timeline=.92f});placed=true;break;
                    }
                    if(!placed)omitted.Add(new{name=prototype.name,kind,original=A(original),reason="No safe team-rule position on the same building, or within16m beside the same street",rejections=rejects});
                }
                int count=green.childCount+props.childCount;var newProps=expansion.transform.Find("StreetDetails");
                TeamMapVerification.Write("restoration_report.json",new{originalBefore=30,originalAfter=count,restoredCount=restored.Count,omittedCount=omitted.Count,expansionCount=newProps.childCount,restored,omitted});TeamMapVerification.Write("restoration_views.json",shots);
                if(newProps.childCount!=190||count>60)throw new InvalidOperationException("Original/expansion count constraint");
                var expandedView=JArray.Parse(File.ReadAllText(FilePath("views.json"))).Single(v=>(string)v["label"]=="expanded_A_Expanded_001");var clone=expansion.GetComponentsInChildren<MeshRenderer>().Single(r=>r.name=="A_Expanded_001").transform;
                Vector3 Read(JToken p)=>new Vector3((float)p[0],(float)p[1],(float)p[2]);
                TeamMapVerification.Write("team_E02_view_local.json",new{source="E02",position=A(clone.InverseTransformPoint(Read(expandedView["position"]))),look=A(clone.InverseTransformPoint(Read(expandedView["look"]))),timeline=.92f});
                EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);Compare();TeamMapPlacement.Audit(scene,count+190);
            }
            finally{PrefabUtility.UnloadPrefabContents(source);}
            Debug.Log("TEAM_MAP_RESTORATION_PASS");
        }
        public static void FixHiddenMounts()
        {
            var scene=EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);Physics.SyncTransforms();
            var report=JObject.Parse(File.ReadAllText(FilePath("restoration_report.json")));var views=JArray.Parse(File.ReadAllText(FilePath("restoration_views.json")));var map=GameObject.Find("MyeongjiMap");var buildings=TeamMapDetailInstaller.Buildings(map);var blocker=TeamMapPlacement.CreateBlocker(scene);var ground=map.transform.Find("Ground").GetComponentInChildren<MeshCollider>();var repairs=new List<object>();
            foreach(var record in (JArray)report["restored"])
            {
                string name=(string)record["name"];var prop=map.GetComponentsInChildren<Transform>().Single(t=>t.name==name);var box=prop.GetComponent<BoxCollider>();var local=new Bounds(box.center,box.size);var renderer=buildings[name.Split('_')[0]];var original=record["from"];var from=new Vector3((float)original[0],(float)original[1],(float)original[2]);
                var query=new GameObject("TeamVisibleWallQuery");query.transform.SetPositionAndRotation(renderer.transform.position,renderer.transform.rotation);query.transform.localScale=renderer.transform.lossyScale;var visible=query.AddComponent<MeshCollider>();visible.sharedMesh=renderer.GetComponent<MeshFilter>().sharedMesh;Physics.SyncTransforms();
                try
                {
                    float height=Kind(prop)=="Mailbox"?1.35f:.45f;var normal=prop.forward;var ray=new Ray(new Vector3(prop.position.x,height,prop.position.z)+normal*4,-normal);
                    if(!visible.Raycast(ray,out var face,8))continue;var bounds=box.bounds;float back=Vector3.Dot(bounds.center,normal)-Mathf.Abs(Vector3.Dot(bounds.extents,normal));if(back>=Vector3.Dot(face.point,normal)-.01f)continue;
                    var candidates=new List<(Vector3 p,Quaternion q)>();var b=renderer.bounds;var physical=renderer.GetComponent<MeshCollider>();
                    foreach(var n in new[]{Vector3.forward,Vector3.back,Vector3.left,Vector3.right})
                    {
                        var lateral=Mathf.Abs(n.z)>.5f?Vector3.right:Vector3.forward;float edge=Vector3.Dot(b.center,n)+Mathf.Abs(Vector3.Dot(b.extents,n));float low=Vector3.Dot(b.center,lateral)-Mathf.Abs(Vector3.Dot(b.extents,lateral));float high=Vector3.Dot(b.center,lateral)+Mathf.Abs(Vector3.Dot(b.extents,lateral));
                        for(float along=low+local.size.x*.5f+.12f;along<=high-local.size.x*.5f-.12f;along+=.15f)
                        {
                            var r=new Ray(lateral*along+n*(edge+2)+Vector3.up*height,-n);if(!visible.Raycast(r,out var hit,8))continue;float wall=Vector3.Dot(hit.point,n);if(physical.Raycast(r,out var oldWall,8))wall=Mathf.Max(wall,Vector3.Dot(oldWall.point,n));
                            candidates.Add((lateral*along+n*(wall+local.size.z*.5f+.08f)+Vector3.up*prop.position.y,Quaternion.LookRotation(n)));
                        }
                    }
                    bool found=false;var old=prop.position;
                    foreach(var c in candidates.OrderBy(c=>(c.p-from).sqrMagnitude))
                    {
                        var target=TeamMapPlacement.RotateBounds(local,c.p,c.q);if(blocker(target)!=null)continue;
                        if(Physics.OverlapBox(target.center,target.extents,Quaternion.identity,~0,QueryTriggerInteraction.Ignore).Any(col=>col!=box&&col!=visible&&col!=ground))continue;
                        if(!ground.Raycast(new Ray(c.p+Vector3.up*30,Vector3.down),out var floor,60)||Mathf.Abs(floor.point.y)>.04f)continue;
                        prop.SetPositionAndRotation(c.p,c.q);Physics.SyncTransforms();record["to"]=JArray.FromObject(A(c.p));record["distance"]=Vector3.Distance(from,c.p);record["mountCheck"]="Outside both original collider and team visible mesh";
                        var shot=views.Single(v=>(string)v["label"]=="restored_"+name);var look=target.center;look.y=Mathf.Max(.65f,look.y);shot["position"]=JArray.FromObject(A(look+prop.forward*4+Vector3.up*.9f));shot["look"]=JArray.FromObject(A(look));shot["objectPosition"]=JArray.FromObject(A(c.p));repairs.Add(new{name,from=A(old),to=A(c.p)});found=true;break;
                    }
                    if(!found)throw new InvalidOperationException("No visible safe mount for restored prop "+name);
                }
                finally{UnityEngine.Object.DestroyImmediate(query);}
            }
            TeamMapVerification.Write("restoration_report.json",report);TeamMapVerification.Write("restoration_views.json",views);TeamMapVerification.Write("visible_mount_repairs.json",repairs);EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);Verify();Debug.Log("TEAM_MAP_VISIBLE_MOUNTS_PASS repairs="+repairs.Count);
        }
        internal static void AdjustCaptureViews(JArray views)
        {
            var objects=GameObject.Find("MyeongjiMap").GetComponentsInChildren<Transform>(true).ToDictionary(t=>TeamMapVerification.PathOf(t));
            foreach(var view in views)
            {
                string name=((string)view["label"]).Substring("restored_".Length);var prop=objects.Values.Single(t=>t.name==name);var collider=prop.GetComponent<BoxCollider>();var target=collider.bounds.center;target.y=Mathf.Max(.55f,target.y);
                var old=view["position"];var outward=Vector3.ProjectOnPlane(new Vector3((float)old[0],(float)old[1],(float)old[2])-target,Vector3.up).normalized;
                float best=float.NegativeInfinity;Vector3 eye=target+outward+Vector3.up*.3f;
                foreach(float angle in new[]{0f,25,-25,50,-50,75,-75})
                {
                    var direction=(Quaternion.Euler(0,angle,0)*outward+Vector3.up*.24f).normalized;float distance=4;
                    foreach(var hit in Physics.SphereCastAll(target,.10f,direction,4,~0,QueryTriggerInteraction.Ignore))if(hit.collider!=collider&&!hit.collider.transform.IsChildOf(GameObject.Find("Player").transform))distance=Mathf.Min(distance,Mathf.Max(.3f,hit.distance-.18f));
                    float score=distance-Mathf.Abs(angle)*.015f;if(score<=best)continue;best=score;eye=target+direction*distance;
                }
                view["position"]=JArray.FromObject(A(eye));view["look"]=JArray.FromObject(A(target));
            }
        }
        internal static void Compare()
        {
            TeamMapVerification.CompareProtected();var before=JObject.Parse(File.ReadAllText(FilePath("followup_before.json")));var after=JObject.Parse(TeamMapVerification.Snapshot().ToString());var differences=new List<string>();
            foreach(var p in before)if(!after.TryGetValue(p.Key,out var value)||!JToken.DeepEquals(p.Value,value))differences.Add((string)p.Value["path"]+" "+p.Key);
            bool expansionSame=JToken.DeepEquals(JObject.Parse(File.ReadAllText(FilePath("followup_expansion_before.json"))),JObject.Parse(Expansion().ToString()));
            TeamMapVerification.Write("followup_compare.json",new{existingItems=before.Count,differences,expansionMeshMaterialsUnchanged=expansionSame});
            if(differences.Count>0||!expansionSame)throw new InvalidOperationException("Follow-up preservation failed "+string.Join(";",differences.Take(5)));
        }
        public static void Verify()
        {
            var scene=EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);Physics.SyncTransforms();Compare();var map=GameObject.Find("MyeongjiMap");int count=map.transform.Find("StreetGreenery").childCount+map.transform.Find("EntranceDetails").childCount;TeamMapPlacement.Audit(scene,count+190);Debug.Log("TEAM_MAP_RESTORATION_SAVED_PASS");
        }
        public static void Cleanup()
        {
            var dependencies=AssetDatabase.GetDependencies(MeasureRunner.ScenePath,true);if(dependencies.Contains(Reference))throw new InvalidOperationException("Scene depends on temporary props");if(!AssetDatabase.DeleteAsset(Reference))throw new InvalidOperationException("Temporary props removal failed");AssetDatabase.SaveAssets();Verify();
        }
        private static float Distance(Vector3 p,Vector3 a,Vector3 b){p.y=a.y=b.y=0;var d=b-a;return Vector3.Distance(p,a+d*Mathf.Clamp01(Vector3.Dot(p-a,d)/Mathf.Max(.0001f,d.sqrMagnitude)));}
    }
}
