using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using DeliveryRider;
public static class MyeongjiEvents
{
 const string Target="Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity",Data="Docs/MapLayout/Myeongji";
 [Serializable] public class BakeData {public HazardCandidates.Spot[] spots;public HazardCandidate[] candidates;}
 static Scene Scene()=>SceneManager.GetActiveScene();
 static GameObject Root(string n)=>Scene().GetRootGameObjects().Single(g=>g.name==n);
 static MeshCollider ground;
 static List<Vector3> keep;
 static Vector3 home;
 static JToken roads;
 static readonly List<HazardCandidates.Spot> spots=new List<HazardCandidates.Spot>();
 static readonly List<HazardCandidate> candidates=new List<HazardCandidate>();
 static void Guard(){if(EditorApplication.isPlaying||EditorApplication.isCompiling||Scene().path!=Target)throw new Exception("Open Myeongji in Edit mode.");}
 static Vector3 P(JToken a)=>new Vector3((float)a[0],0,(float)a[1]);
 static Vector3 Flat(Vector3 p)=>new Vector3(p.x,0,p.z);
 static Vector3 Project(Vector3 p,Vector3 a,Vector3 b){var d=b-a;return a+d*Mathf.Clamp01(Vector3.Dot(p-a,d)/d.sqrMagnitude);}
 static bool Ground(Vector3 p,out Vector3 feet){feet=p;if(!ground.Raycast(new Ray(new Vector3(p.x,25,p.z),Vector3.down),out var hit,40))return false;feet=hit.point;return true;}
 static bool Clear(Vector3 p,float radius=.65f,float height=2f)
 {
  if(!Ground(p,out var feet))return false;
  return !Physics.OverlapCapsule(feet+Vector3.up*(radius+.07f),feet+Vector3.up*Mathf.Max(radius+.07f,height-radius),radius,~0,QueryTriggerInteraction.Ignore).Any(c=>c!=ground&&c.attachedRigidbody==null);
 }
 static bool Away(Vector3 p,float clearance=4f)=>Vector3.Distance(Flat(p),Flat(home))>=11f&&keep.All(k=>Vector3.Distance(Flat(p),Flat(k))>=clearance);
 static bool Junction(Vector3 p,JToken self)=>roads.Any(r=>r!=self&&Vector3.Distance(Flat(p),Project(Flat(p),P(r["a"]),P(r["b"])))<(float)r["width"]*.5f+2.5f);
 static bool LineClear(Vector3 a,Vector3 b,float radius=.6f)
 {
  int n=Mathf.CeilToInt(Vector3.Distance(a,b)/.6f);
  for(int i=0;i<=n;i++)if(!Clear(Vector3.Lerp(a,b,i/(float)Mathf.Max(1,n)),radius))return false;
  return true;
 }
 static void Add(string type,int spot,JToken road,Vector3 p,Quaternion rot,int side,bool flip=false)
 {
  if(!Away(p))return;
  candidates.Add(new HazardCandidate{type=type,spot=spot,position=p,rotation=rot,halfWidth=(float)road["width"]*.5f,side=side,alley=(float)road["width"]<5f,flip=flip});
 }
 public static string Bake()
 {
  Guard();if(Scene().isDirty)throw new Exception("Save scene edits before baking.");
  var backup="Assets/_Game/Scenes/FourDistrictPrototype_Myeongji_BeforeEvents.unity";
  if(AssetDatabase.LoadAssetAtPath<SceneAsset>(backup)==null&&!AssetDatabase.CopyAsset(Target,backup))throw new Exception("Backup failed.");
  ground=Root("MyeongjiMap").transform.Find("Ground").GetComponentInChildren<MeshCollider>();
  var layout=JObject.Parse(File.ReadAllText(Data+"/layout.json"));roads=layout["roads"];
  keep=Root("MyeongjiLocations").transform.Cast<Transform>().Select(t=>t.position).ToList();home=Root("MyeongjiLocations").transform.Find("C_Home").position;
  spots.Clear();candidates.Clear();Physics.SyncTransforms();
  float pile=RollingMelonHazard.PileHalfExtent(Root("Player").GetComponent<CapsuleCollider>().height*.3f);
  foreach(var road in roads)
  {
   var a=P(road["a"]);var b=P(road["b"]);var along=(b-a).normalized;var right=Vector3.Cross(Vector3.up,along);float half=(float)road["width"]*.5f;bool alley=half<2.5f;var rot=Quaternion.LookRotation(along);
   for(float d=5f;d<Vector3.Distance(a,b)-5f;d+=4f)
   {
    var center=a+along*d;if(!Ground(center,out center)||!Away(center)||Junction(center,road)||!Clear(center,.6f))continue;
    int spot=spots.Count;spots.Add(new HazardCandidates.Spot{position=center});
    foreach(int side in new[]{-1,1})
    {
     if(!alley||side==1)
     {var lid=center+(alley?Vector3.zero:right*side*half*.5f);if(Ground(lid,out lid)&&Clear(lid,.98f)&&!Junction(lid,road))Add("manhole",spot,road,lid+Vector3.up*.008f,rot,side);}
     var chicken=center+right*side*Mathf.Max(0,half-1.25f);
     if(Ground(chicken,out chicken)&&Clear(chicken,1.1f)&&Away(chicken,7f)&&Vector3.Distance(Flat(chicken),Flat(home))>15f)Add("chicken",spot,road,chicken,rot,side);
     // Resting fruit pile fits entirely beside the road, with a clear escape space along it.
     var melon=center+right*side*(half+pile+.15f);
     if(Ground(melon,out melon)&&Away(melon,5f)&&!Junction(melon,road)&&Clear(melon,pile+.1f)&&LineClear(melon-along*2,melon+along*2,pile))Add("melon",spot,road,melon,rot,side);
     var hydrant=center+right*side*(half+.85f);
     if(Ground(hydrant,out hydrant)&&Clear(hydrant,.85f)&&!Junction(hydrant,road))Add("hydrant",spot,road,hydrant,Quaternion.LookRotation(-right*side),side);
     // Wall-mounted door: local +X swings towards the street, local +Z lies along the facade.
     var outward=right*side;
     var wallHits=Physics.RaycastAll(center+Vector3.up*1.3f,outward,half+6f,~0,QueryTriggerInteraction.Ignore).Where(h=>h.collider!=ground&&h.collider.attachedRigidbody==null&&h.collider.transform.IsChildOf(Root("MyeongjiMap").transform)&&h.collider is MeshCollider).OrderBy(h=>h.distance).ToArray();
     if(wallHits.Length>0)
     {
      var hit=wallHits[0];var forward=side<0?along:-along;var facing=Quaternion.LookRotation(forward);var origin=hit.point-outward*.24f-forward*1.2f;
      if(Ground(origin,out origin)&&Away(origin,5f))
      {
       bool clear=true;
       // Validate the complete swing sector rather than just the hinge point.
       for(int ai=0;ai<=6&&clear;ai++)for(int ri=1;ri<=6&&clear;ri++)
       {float ang=ai*Mathf.PI/12f,rr=ri*.4f;var p=origin+facing*new Vector3(Mathf.Sin(ang)*rr,0,Mathf.Cos(ang)*rr);clear=Clear(p,.12f,2.4f)&&Away(p,3.8f);}
       if(clear)Add("door",spot,road,origin+Vector3.up*.03f,facing,side);
      }
     }
     var tireStart=center+right*side*(half+(alley?.5f:2.5f));var tireEnd=center-right*side*(half+1f);
     if(Away(tireStart,5f)&&Away(tireEnd,5f)&&LineClear(tireStart,tireEnd,.65f))Add("tire",spot,road,center,rot,side);
     var duckStart=center+right*side*(half+2f);var duckEnd=center-right*side*(half+2f);
     if(Away(duckStart,5f)&&Away(duckEnd,5f)&&LineClear(duckStart,duckEnd,.5f)&&LineClear(duckStart-along*1.5f,duckStart+along*1.5f,.5f))Add("duck",spot,road,center,rot,side);
     var pole=center+right*side*(half+1f);
     if(!Ground(pole,out pole)||!Clear(pole,.5f)||!Away(pole,5f))continue;
     foreach(bool flip in new[]{false,true})
     {
      bool clear=true;var direction=flip?-along:along;
      for(int i=0;i<16&&clear;i++)
      {
       var p=center+direction*(4+i*3);var near=roads.Any(r=>Vector3.Distance(Flat(p),Project(Flat(p),P(r["a"]),P(r["b"])))<=(float)r["width"]*.5f);
       clear=near&&Ground(p,out var feet)&&!Physics.CheckSphere(feet+Vector3.up*1.5f,.45f,~0,QueryTriggerInteraction.Ignore);
      }
      if(clear)Add("theft",spot,road,pole,rot,side,flip);
     }
    }
   }
  }
  File.WriteAllText(Data+"/hazard_candidates.json",JsonUtility.ToJson(new BakeData{spots=spots.ToArray(),candidates=candidates.ToArray()},true));
  return Newtonsoft.Json.JsonConvert.SerializeObject(new{spots=spots.Count,types=candidates.GroupBy(c=>c.type).ToDictionary(g=>g.Key,g=>g.Count())});
 }
 public static string InstallAssets()
 {
  Guard();var bake=JsonUtility.FromJson<BakeData>(File.ReadAllText(Data+"/hazard_candidates.json"));
  if(bake.spots.Length==0||bake.candidates.Any(c=>c.spot<0||c.spot>=bake.spots.Length))throw new Exception("Invalid baked candidate data.");
  const string candidatePath="Assets/_Game/Settings/MyeongjiHazardCandidates.asset";
  var asset=AssetDatabase.LoadAssetAtPath<HazardCandidates>(candidatePath);
  if(asset==null){asset=ScriptableObject.CreateInstance<HazardCandidates>();AssetDatabase.CreateAsset(asset,candidatePath);}
  asset.Configure(bake.spots,bake.candidates);EditorUtility.SetDirty(asset);
  const string settingsPath="Assets/_Game/Settings/MyeongjiHazardSpawnSettings.asset";
  if(AssetDatabase.LoadAssetAtPath<HazardSpawnSettings>(settingsPath)==null&&!AssetDatabase.CopyAsset("Assets/_Game/Settings/HazardSpawnSettings.asset",settingsPath))throw new Exception("Could not copy hazard settings.");
  var settings=AssetDatabase.LoadAssetAtPath<HazardSpawnSettings>(settingsPath);
  // The original door carries a demo wall box. The imported town already provides its wall.
  const string doorPath="Assets/_Game/Prefabs/Myeongji/SwingDoor.prefab";
  if(AssetDatabase.LoadAssetAtPath<GameObject>(doorPath)==null)
  {
   if(!AssetDatabase.CopyAsset("Assets/_Game/Prefabs/Hazards/SwingDoor.prefab",doorPath))throw new Exception("Could not copy door prefab.");
   var door=PrefabUtility.LoadPrefabContents(doorPath);
   try{var backing=door.transform.Find("Backing");if(backing!=null)UnityEngine.Object.DestroyImmediate(backing.gameObject);PrefabUtility.SaveAsPrefabAsset(door,doorPath);}
   finally{PrefabUtility.UnloadPrefabContents(door);}
  }
  var kinds=settings.Kinds.ToArray();kinds.Single(k=>k.type=="door").prefab=AssetDatabase.LoadAssetAtPath<GameObject>(doorPath);settings.Configure(kinds);EditorUtility.SetDirty(settings);
  foreach(var kind in kinds)
  {
   if(kind.prefab==null||!bake.candidates.Any(c=>c.type==kind.type))throw new Exception("Missing asset/candidates: "+kind.type);
   if(!kind.prefab.GetComponents<MonoBehaviour>().Any(c=>c is IPlacedHazard))throw new Exception("Missing placement handler: "+kind.type);
  }
  AssetDatabase.SaveAssets();
  var crowd=DeliveryRider.Editor.CelebrityCrowdInstaller.ApplyToScene(Scene());
  var pigeons=AssetDatabase.LoadAssetAtPath<Sprite>(DeliveryRider.Editor.PigeonFlockInstaller.IconPath)!=null
   ? DeliveryRider.Editor.PigeonFlockInstaller.ApplyToScene(Scene()) : "";
  return "Saved Myeongji-only candidate/settings assets and wall-free door prefab; original day counts and unlock days retained. "+crowd+" "+pigeons;
 }
 public static string Activate()
 {
  Guard();var planner=Root("Hazards").GetComponent<HazardPlanner>();var so=new SerializedObject(planner);
  so.FindProperty("session").objectReferenceValue=Root("DeliverySession").GetComponent<DeliverySession>();
  so.FindProperty("rider").objectReferenceValue=Root("Player").GetComponent<RiderMotor>();
  so.FindProperty("ground").objectReferenceValue=Root("MyeongjiRoutes").GetComponent<RoadNetwork>();
  so.FindProperty("settings").objectReferenceValue=AssetDatabase.LoadAssetAtPath<HazardSpawnSettings>("Assets/_Game/Settings/MyeongjiHazardSpawnSettings.asset");
  so.FindProperty("candidates").objectReferenceValue=AssetDatabase.LoadAssetAtPath<HazardCandidates>("Assets/_Game/Settings/MyeongjiHazardCandidates.asset");
  so.ApplyModifiedPropertiesWithoutUndo();planner.enabled=true;Root("Hazards").SetActive(true);
  EditorSceneManager.MarkSceneDirty(Scene());if(!EditorSceneManager.SaveScene(Scene()))throw new Exception("Scene save failed.");AssetDatabase.SaveAssets();
  return Review();
 }
 public static string Review()
 {
  Guard();var planner=Root("Hazards").GetComponent<HazardPlanner>();var so=new SerializedObject(planner);
  var settings=(HazardSpawnSettings)so.FindProperty("settings").objectReferenceValue;var data=(HazardCandidates)so.FindProperty("candidates").objectReferenceValue;
  var missing=new[]{"session","rider","ground","settings","candidates"}.Where(n=>so.FindProperty(n).objectReferenceValue==null).ToArray();
  return Newtonsoft.Json.JsonConvert.SerializeObject(new{
   scene=Scene().path,active=planner.isActiveAndEnabled,missingReferences=missing,
   candidates=data.Candidates.Count,types=settings.Kinds.Select(k=>new{type=k.type,unlockDay=k.unlockDay,day1=k.Count(1),day4=k.Count(4),candidates=data.Candidates.Count(c=>c.type==k.type),prefab=AssetDatabase.GetAssetPath(k.prefab)}).ToArray(),
   ground=so.FindProperty("ground").objectReferenceValue.name,
   traffic=Root("Traffic").activeSelf,pedestrians=Root("Pedestrians").activeSelf,
   saved=!Scene().isDirty,playMode=EditorApplication.isPlaying});
 }

}
