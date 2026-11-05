using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Debug=UnityEngine.Debug;

namespace DeliveryRider.Editor
{
    [InitializeOnLoad]
    public static class TeamMapVerification
    {
        internal const string Output=@"C:\UnityWork\ge\공유\결과\팀맵";
        private const string Pending="TeamMap.Verification";
        static TeamMapVerification()=>EditorApplication.playModeStateChanged+=Mode;
        public static void Baseline()
        {
            Directory.CreateDirectory(Output);EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);
            Write("protected_before.json",Snapshot());Write("game_assets_before.json",GameAssets());MakeViews();
            SessionState.SetString(Pending+".stage","before");SessionState.SetBool(Pending,true);EditorApplication.EnterPlaymode();
        }
        public static void After()
        {
            Directory.CreateDirectory(Output);EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);CompareProtected();
            SessionState.SetString(Pending+".stage","after");SessionState.SetBool(Pending,true);EditorApplication.EnterPlaymode();
        }
        public static void Restored()
        {
            TeamMapOriginalRestoration.Verify();var shots=JArray.Parse(File.ReadAllText(Path.Combine(Output,"restoration_views.json")));TeamMapOriginalRestoration.AdjustCaptureViews(shots);
            foreach(var view in JArray.Parse(File.ReadAllText(Path.Combine(Output,"views.json"))).Where(v=>(string)v["label"]=="original_E06"||(string)v["label"]=="expanded_A_Expanded_001"))
            {var copy=(JObject)view.DeepClone();copy["label"]="followup_"+(string)view["label"];shots.Add(copy);}
            Write("restoration_capture_views.json",shots);SessionState.SetString(Pending+".stage","restored");SessionState.SetBool(Pending,true);EditorApplication.EnterPlaymode();
        }
        private static void Mode(PlayModeStateChange mode)
        {
            if(!SessionState.GetBool(Pending,false))return;
            if(mode==PlayModeStateChange.EnteredPlayMode)new GameObject("TeamMapVisualProbe").AddComponent<TeamMapVisualProbe>();
            if(mode==PlayModeStateChange.EnteredEditMode){SessionState.SetBool(Pending,false);EditorApplication.Exit(SessionState.GetInt(Pending+".exit",1));}
        }
        internal static string Stage=>SessionState.GetString(Pending+".stage","before");
        internal static void Finish(Exception error)
        {
            Debug.Log(error==null?"TEAM_MAP_VISUAL_PASS stage="+Stage:"TEAM_MAP_VISUAL_FAIL "+error);SessionState.SetInt(Pending+".exit",error==null?0:1);EditorApplication.ExitPlaymode();
        }
        internal static void Write(string file,object value)=>File.WriteAllText(Path.Combine(Output,file),JsonConvert.SerializeObject(value,Formatting.Indented));
        internal static string PathOf(Transform t)=>t.parent==null?t.name:PathOf(t.parent)+"/"+t.name;
        private static string Ref(UnityEngine.Object o)=>o==null?"null":GlobalObjectId.GetGlobalObjectIdSlow(o).ToString();
        internal static JObject Snapshot()
        {
            var result=new JObject();var scene=SceneManager.GetActiveScene();
            foreach(var root in scene.GetRootGameObjects())foreach(var t in root.GetComponentsInChildren<Transform>(true))
            {
                string key=Ref(t);result[key]=new JObject{["path"]=PathOf(t),["position"]=JArray.FromObject(new[]{t.localPosition.x,t.localPosition.y,t.localPosition.z}),["rotation"]=JArray.FromObject(new[]{t.localRotation.x,t.localRotation.y,t.localRotation.z,t.localRotation.w}),["scale"]=JArray.FromObject(new[]{t.localScale.x,t.localScale.y,t.localScale.z}),["active"]=t.gameObject.activeSelf};
                foreach(var component in t.GetComponents<Component>())
                {
                    if(!(component is MonoBehaviour)&&!(component is Collider)&&!(component is Light)&&!(component is Rigidbody)&&!(component is Camera))continue;
                    if(component==null)throw new InvalidOperationException("Missing script in our scene: "+PathOf(t));
                    var fields=new JObject();var so=new SerializedObject(component);var p=so.GetIterator();bool enter=true;while(p.Next(enter))
                    {
                        enter=p.propertyType!=SerializedPropertyType.ObjectReference&&p.propertyType!=SerializedPropertyType.String;
                        string value=null;switch(p.propertyType)
                        {
                            case SerializedPropertyType.ObjectReference:value=Ref(p.objectReferenceValue);break;
                            case SerializedPropertyType.Integer:case SerializedPropertyType.ArraySize:value=p.longValue.ToString(CultureInfo.InvariantCulture);break;
                            case SerializedPropertyType.LayerMask:case SerializedPropertyType.Enum:value=p.intValue.ToString(CultureInfo.InvariantCulture);break;
                            case SerializedPropertyType.Boolean:value=p.boolValue.ToString();break;
                            case SerializedPropertyType.Float:value=p.doubleValue.ToString("R",CultureInfo.InvariantCulture);break;
                            case SerializedPropertyType.String:value=p.stringValue;break;
                            case SerializedPropertyType.Vector2:value=p.vector2Value.ToString("R");break;
                            case SerializedPropertyType.Vector3:value=p.vector3Value.ToString("R");break;
                            case SerializedPropertyType.Vector4:value=p.vector4Value.ToString("R");break;
                            case SerializedPropertyType.Quaternion:value=p.quaternionValue.ToString("R");break;
                            case SerializedPropertyType.Color:value=p.colorValue.ToString("R");break;
                            case SerializedPropertyType.Rect:value=p.rectValue.ToString("R");break;
                            case SerializedPropertyType.Bounds:value=p.boundsValue.ToString("R");break;
                        }
                        if(value!=null)fields[p.propertyPath]=value;
                    }
                    result[Ref(component)]=new JObject{["path"]=PathOf(t),["type"]=component.GetType().FullName,["fields"]=fields};
                }
            }
            return result;
        }
        private static Dictionary<string,string> GameAssets()
        {
            var paths=Directory.GetFiles("Assets/_Game/Settings","*",SearchOption.AllDirectories).Where(p=>!p.EndsWith(".meta"))
                .Concat(Directory.GetFiles("Assets/_Game/Prefabs/Hazards","*",SearchOption.AllDirectories).Where(p=>!p.EndsWith(".meta")))
                .Concat(Directory.GetFiles("Docs/MapLayout/Myeongji","*.json",SearchOption.TopDirectoryOnly));
            return paths.OrderBy(p=>p).ToDictionary(p=>p,p=>BitConverter.ToString(SHA256.Create().ComputeHash(File.ReadAllBytes(p))).Replace("-",""));
        }
        public static void CompareProtected()
        {
            var before=JObject.Parse(File.ReadAllText(Path.Combine(Output,"protected_before.json")));var after=JObject.Parse(Snapshot().ToString());Write("protected_after.json",after);var differences=new List<string>();
            foreach(var pair in before)if(!after.TryGetValue(pair.Key,out var current)||!JToken.DeepEquals(pair.Value,current))differences.Add((string)pair.Value["path"]+" "+(string)pair.Value["type"]+" ["+pair.Key+"]");
            var assets=JObject.Parse(File.ReadAllText(Path.Combine(Output,"game_assets_before.json")));var currentAssets=GameAssets();foreach(var pair in assets)if(!currentAssets.TryGetValue(pair.Key,out string sha)||sha!=(string)pair.Value)differences.Add("asset "+pair.Key);
            Write("protected_compare.json",new{beforeComponents=before.Count,afterComponents=after.Count,differences});
            if(differences.Count>0)throw new InvalidOperationException("Protected values changed: "+string.Join("; ",differences.Take(10)));
            Debug.Log("TEAM_MAP_VALUES_PASS existing objects="+before.Count+" gameplay assets="+assets.Count);
        }
        private static void MakeViews()
        {
            var layout=JObject.Parse(File.ReadAllText("Docs/MapLayout/Myeongji/layout.json"));var lots=(JArray)layout["lots"];var expanded=(JArray)JObject.Parse(File.ReadAllText("Docs/MapLayout/Myeongji/lots_expanded.json"))["lots"];
            var map=GameObject.Find("MyeongjiMap");var ext=GameObject.Find("MyeongjiMapExpansion");var views=new List<object>();
            void Add(string label,Renderer renderer,Vector3 outward)
            {
                var center=renderer.bounds.center;center.y=1.4f;float radius=Mathf.Abs(outward.x)*renderer.bounds.extents.x+Mathf.Abs(outward.z)*renderer.bounds.extents.z;
                var target=center+outward*radius;var eye=target+outward*8+Vector3.up*.7f;
                views.Add(new{label,position=new[]{eye.x,eye.y,eye.z},look=new[]{target.x,target.y,target.z},timeline=.92f});
            }
            Vector3 Normal(float angle)=>angle==0?Vector3.back:angle==180?Vector3.forward:angle==90?Vector3.right:Vector3.left;
            foreach(string id in new[]{"A06","B05","E06"})
            {
                var lot=lots.Single(l=>(string)l[0]==id);var r=map.GetComponentsInChildren<MeshRenderer>().Single(r=>r.name=="AB01_"+id+".001");Add("original_"+id,r,Normal((float)lot[7]));
            }
            foreach(string zone in new[]{"A","D","C"})
            {
                var lot=expanded.First(l=>(string)l["zone"]==zone&&(string)l["facesAnchor"]!=null);string id=(string)lot["id"];var r=ext.GetComponentsInChildren<MeshRenderer>().Single(r=>r.name==id);Add("expanded_"+id,r,Normal((float)lot["rotationDeg"]));
            }
            Write("views.json",views);
        }
    }
    public sealed class TeamMapVisualProbe:MonoBehaviour
    {
        private const BindingFlags Any=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        private IEnumerator probe;private readonly List<object> rows=new List<object>();
        private void Start()=>probe=Probe();
        private void Update(){try{if(probe!=null&&!probe.MoveNext()){probe=null;Save();TeamMapVerification.Finish(null);}}catch(Exception e){probe=null;Save();TeamMapVerification.Finish(e);}}
        private IEnumerator Probe()
        {
            yield return null;var session=FindFirstObjectByType<DeliverySession>();var planner=FindFirstObjectByType<HazardPlanner>();var rider=session.Player.GetComponent<RiderMotor>();
            Set(planner,"randomEachRun",false);Set(planner,"seed",111);rider.SetInputBlocked(true);rider.enabled=false;var body=rider.GetComponent<Rigidbody>();body.interpolation=RigidbodyInterpolation.None;body.isKinematic=true;
            foreach(int day in TeamMapVerification.Stage=="restored"?new[]{4}:new[]{1,4})
            {
                Property(session,"Progress",new RunProgress(day,100000));Set(session,"openZones",session.Economy.ZonesForDay(day));Set(planner,"day",day);var timer=Stopwatch.StartNew();planner.Respawn();timer.Stop();
                rows.Add(new{kind="dayStartHazardRespawn",day,ms=timer.Elapsed.TotalMilliseconds,slots=planner.Slots.Count});yield return null;
            }
            if(TeamMapVerification.Stage=="restored")session.GetType().GetMethod("Notify",Any).Invoke(session,null);
            Time.timeScale=0f;planner.enabled=false;var camera=Camera.main;foreach(var control in camera.GetComponents<ThirdPersonCamera>())control.enabled=false;
            var sky=FindFirstObjectByType<DaySky>();sky.Preview=.92f;sky.PreviewSpeed=0f;
            var views=JArray.Parse(File.ReadAllText(Path.Combine(TeamMapVerification.Output,(TeamMapVerification.Stage=="restored"?"restoration_capture_views.json":"views.json"))));
            var target=new RenderTexture(1920,1080,24,RenderTextureFormat.ARGB32);target.Create();var pixels=new Texture2D(1920,1080,TextureFormat.RGB24,false);
            try
            {
                foreach(var view in views)
                {
                    camera.transform.position=V(view["position"]);camera.transform.LookAt(V(view["look"]));camera.fieldOfView=65;body.position=TeamMapVerification.Stage=="restored"&&!((string)view["label"]).StartsWith("followup_")?camera.transform.position-camera.transform.forward*1.5f:V(view["look"])-camera.transform.forward*2;body.position=new Vector3(body.position.x,1,body.position.z);session.Player.position=body.position;session.Player.rotation=Quaternion.LookRotation(Vector3.ProjectOnPlane(camera.transform.forward,Vector3.up));Physics.SyncTransforms();
                    for(int warm=0;warm<30;warm++)yield return null;
                    if(Vector3.Distance(camera.transform.position,V(view["position"]))>.01f||Vector3.Distance(session.Player.position,body.position)>.01f)throw new InvalidOperationException("Frozen comparison pose drifted");
                    var cpu=new List<double>();var frame=new List<double>();var request=new UniversalRenderPipeline.SingleCameraRequest{destination=target};var timing=new FrameTiming[1];var gpu=new List<double>();
                    for(int i=0;i<(TeamMapVerification.Stage=="restored"?0:100);i++)
                    {
                        var timer=Stopwatch.StartNew();RenderPipeline.SubmitRenderRequest(camera,request);timer.Stop();cpu.Add(timer.Elapsed.TotalMilliseconds);frame.Add(Time.unscaledDeltaTime*1000d);
                        FrameTimingManager.CaptureFrameTimings();if(FrameTimingManager.GetLatestTimings(1,timing)>0&&timing[0].gpuFrameTime>0)gpu.Add(timing[0].gpuFrameTime);
                        yield return null;
                    }
                    if(TeamMapVerification.Stage=="restored")FindFirstObjectByType<NeighborhoodEveningLights>().PreviewAt(.92f,camera.transform.position);
                    RenderPipeline.SubmitRenderRequest(camera,request);var previous=RenderTexture.active;RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,1920,1080),0,0);pixels.Apply();RenderTexture.active=previous;
                    string label=(string)view["label"];File.WriteAllBytes(Path.Combine(TeamMapVerification.Output,label+"_"+TeamMapVerification.Stage+".png"),pixels.EncodeToPNG());
                    cpu.Sort();frame.Sort();gpu.Sort();if(cpu.Count==0){rows.Add(new{kind="restoredPhoto",label});continue;}rows.Add(new{kind="frame",label,renderSubmitMedianMs=cpu[cpu.Count/2],renderSubmitP95Ms=cpu[(int)(cpu.Count*.95)],frameMedianMs=frame[frame.Count/2],frameP95Ms=frame[(int)(frame.Count*.95)],gpuMedianMs=gpu.Count>0?gpu[gpu.Count/2]:-1,gpuSamples=gpu.Count,activeLights=FindObjectsByType<Light>(FindObjectsSortMode.None).Count(l=>l.enabled&&l.type!=LightType.Directional)});
                }
            }
            finally{Destroy(pixels);target.Release();Destroy(target);Time.timeScale=1f;}
        }
        private static Vector3 V(JToken token)=>new Vector3((float)token[0],(float)token[1],(float)token[2]);
        private void Save()=>TeamMapVerification.Write(TeamMapVerification.Stage=="restored"?"restoration_capture.json":"performance_"+TeamMapVerification.Stage+".json",rows);
        private static void Set(object o,string n,object v)=>o.GetType().GetField(n,Any).SetValue(o,v);
        private static void Property(object o,string n,object v)=>o.GetType().GetProperty(n,Any).SetValue(o,v);
    }
}
