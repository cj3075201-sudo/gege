using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Debug=UnityEngine.Debug;

namespace DeliveryRider.Editor
{
    [InitializeOnLoad]
    public static class HazardPreviewPlayChecks
    {
        private const string Pending="HazardPreview.Pending";
        static HazardPreviewPlayChecks()=>EditorApplication.playModeStateChanged+=OnMode;
        public static void Run()
        {
            EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);
            var planner=UnityEngine.Object.FindFirstObjectByType<HazardPlanner>();
            if(!(bool)Get(planner,"showSelectedBeforePickup"))throw new InvalidOperationException("PREVIEW_DEFAULT_RED: selected hazards must preview before pickup by default");
            SessionState.SetBool(Pending,true);EditorApplication.EnterPlaymode();
        }
        private static object Get(object target,string name)=>target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).GetValue(target);
        private static void OnMode(PlayModeStateChange mode)
        {
            if(!SessionState.GetBool(Pending,false))return;
            if(mode==PlayModeStateChange.EnteredPlayMode)new GameObject("HazardPreviewProbe").AddComponent<HazardPreviewProbe>();
            if(mode==PlayModeStateChange.EnteredEditMode){SessionState.SetBool(Pending,false);EditorApplication.Exit(SessionState.GetInt(Pending+".exit",1));}
        }
        internal static void Finish(Exception e)
        {
            if(e!=null)Debug.LogError("HAZARD_PREVIEW_FAIL "+e);else Debug.Log("HAZARD_PREVIEW_PASS: frozen preview, zero interaction, pickup fade, food recovery, call reset, no material copies");
            SessionState.SetInt(Pending+".exit",e==null?0:1);EditorApplication.ExitPlaymode();
        }
    }
    public sealed class HazardPreviewProbe:MonoBehaviour
    {
        private const BindingFlags Any=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private IEnumerator probe;private readonly List<object> costs=new List<object>();
        private void Start()=>probe=Probe();
        private void Update(){try{if(probe!=null&&!probe.MoveNext()){probe=null;Save();HazardPreviewPlayChecks.Finish(null);}}catch(Exception e){probe=null;Save();HazardPreviewPlayChecks.Finish(e);}}
        private IEnumerator Probe()
        {
            yield return null;
            var session=FindFirstObjectByType<DeliverySession>();var planner=FindFirstObjectByType<HazardPlanner>();var rider=session.Player.GetComponent<RiderMotor>();var body=rider.GetComponent<Rigidbody>();Call(rider,"ResetReaction");rider.enabled=false;body.interpolation=RigidbodyInterpolation.None;body.isKinematic=true;rider.SetInputBlocked(true);Set(rider,"nextZoneCheckAt",1e9f);
            var recovery=rider.GetComponent<RiderSafeRecovery>();if(recovery!=null)recovery.enabled=false;
            Property(session,"Progress",new RunProgress(4,100000));Set(session,"openZones",session.Economy.ZonesForDay(4));Set(planner,"day",4);Set(planner,"randomEachRun",false);Set(planner,"seed",111);planner.Respawn();yield return null;
            Check(Mathf.Approximately((float)Get(planner,"previewOpacity"),.4f),"default opacity .4");Check(Mathf.Approximately((float)Get(planner,"previewFadeSeconds"),.3f),"default fade .3");
            Check(session.OpenCall()&&session.TryAcceptOrder(session.Candidates[0]),"actual call acceptance");planner.enabled=false;
            var all=planner.Slots.ToArray();var ids=all.Select(s=>s.GetInstanceID()).ToArray();
            foreach(var slot in all.Where(s=>s.Role==HazardRole.Trigger)){slot.Selected=false;slot.SetRuntimeActive(false);}
            var chosen=all.Where(s=>s.Role==HazardRole.Trigger).GroupBy(s=>s.Type).Select(g=>g.First()).ToArray();
            foreach(var slot in chosen){slot.Selected=true;slot.NeedsCallReset=true;}
            body.position=new Vector3(1000,30,1000);session.Player.position=body.position;
            bool tireChoice=false;
            var materials=all.SelectMany(s=>s.GetComponentsInChildren<Renderer>(true)).SelectMany(r=>r.sharedMaterials).Where(m=>m!=null).Distinct().ToDictionary(m=>m.GetInstanceID(),m=>EditorJsonUtility.ToJson(m));
            var timer=Stopwatch.StartNew();Call(planner,"ApplyPendingSlots");timer.Stop();costs.Add(new{state="coldPreview12Kinds",ms=timer.Elapsed.TotalMilliseconds});
            tireChoice=(bool)Get(chosen.First(s=>s.Type=="tire").GetComponent<RollingTireHazard>(),"waveLarge");
            Check(chosen.All(Preview),"all selected kinds visible as previews");Check(chosen.All(s=>!s.StrikeAllowed&&!s.RuntimeActive&&!s.StartedFor(session)),"preview cannot strike, run controllers, or consume the call activation");
            foreach(var slot in chosen)
            {
                Check(slot.GetComponentsInChildren<Collider>(true).All(c=>!c.enabled),"preview colliders off "+slot.Type);
                Check(slot.GetComponentsInChildren<MonoBehaviour>(true).Where(c=>c!=null&&(c is IPlacedHazard||c.transform!=slot.transform&&c.GetType().Namespace==nameof(DeliveryRider))).All(c=>!c.enabled),"preview controllers off "+slot.Type);
                var visible=slot.GetComponentsInChildren<Renderer>(false).Where(r=>r.enabled).ToArray();Check(visible.Length>0,"idle preview geometry exists "+slot.Type);
                Check(visible.All(r=>r.shadowCastingMode==ShadowCastingMode.Off),"preview casts no shadow "+slot.Type);
                Check(visible.Where(r=>r.sharedMaterial!=null&&r.sharedMaterial.shader.name=="DeliveryRider/Curved Lit").All(r=>(r.renderingLayerMask&0x40000000u)!=0),"preview data reaches all renderers "+slot.Type);
            }
            Check(all.Where(s=>s.Role==HazardRole.Object).SelectMany(s=>s.GetComponentsInChildren<Renderer>(true)).All(r=>(r.renderingLayerMask&0x40000000u)==0),"objects remain opaque");
            var nodes=chosen.SelectMany(s=>s.GetComponentsInChildren<Transform>(false)).ToArray();var positions=nodes.Select(n=>n.position).ToArray();
            for(int frame=0;frame<20;frame++)yield return null;
            Check(nodes.Select((n,i)=>Vector3.Distance(n.position,positions[i])).All(d=>d<.002f),"preview remains frozen over actual frames");
            Check(chosen.All(s=>s.GetComponentsInChildren<TMPro.TMP_Text>(false).Length==0),"idle previews hide callouts and late-created fallback font submeshes");
            foreach(float opacity in new[]{.2f,1f,.4f})
            {
                DevValue.Find(planner,"previewOpacity","preview opacity").Write(planner,opacity);Call(planner,"ApplyPendingSlots");
                Check(chosen.All(s=>Mathf.Approximately((float)Prop(s,"GhostOpacity"),opacity)&&!s.StrikeAllowed&&!s.RuntimeActive),"F1 opacity changes visuals only");
                foreach(var slot in chosen)foreach(var renderer in slot.GetComponentsInChildren<Renderer>(false).Where(r=>r.enabled))Check(renderer.shadowCastingMode==ShadowCastingMode.Off,$"preview shadows stay off opacity={opacity} kind={slot.Type} renderer={renderer.name} mask={renderer.renderingLayerMask:x} preview={slot.IsPreviewVisible} actual={slot.GhostOpacity}");
            }
            Set(planner,"showSelectedBeforePickup",false);timer.Restart();Call(planner,"ApplyPendingSlots");timer.Stop();costs.Add(new{state="hidePreview12Kinds",ms=timer.Elapsed.TotalMilliseconds});Check(chosen.All(s=>!Preview(s)&&!s.RuntimeActive&&!s.StrikeAllowed),"F1 off returns to hidden pre-pickup behavior");
            Set(planner,"showSelectedBeforePickup",true);timer.Restart();Call(planner,"ApplyPendingSlots");timer.Stop();costs.Add(new{state="warmPreview12Kinds",ms=timer.Elapsed.TotalMilliseconds});
            Check((bool)Get(chosen.First(s=>s.Type=="tire").GetComponent<RollingTireHazard>(),"waveLarge")==tireChoice,"same-call preview toggles retain tire size choice");
            var duck=chosen.First(s=>s.Type=="duck");var chicken=all.Where(s=>s.Type=="chicken").OrderBy(s=>(s.transform.position-duck.transform.position).sqrMagnitude).First();
            foreach(var slot in chosen){slot.Selected=slot==duck;}
            chicken.Selected=true;chicken.NeedsCallReset=true;Call(planner,"ApplyPendingSlots");
            var camera=Camera.main;foreach(var control in camera.GetComponents<MonoBehaviour>())if(control is ThirdPersonCamera)control.enabled=false;
            var center=duck.transform.position+Vector3.up*.6f;camera.transform.position=center-duck.transform.forward*16+duck.transform.right*7+Vector3.up*11;camera.transform.LookAt(center);camera.fieldOfView=65;
            body.position=center+Vector3.up*50;session.Player.position=body.position;
            yield return null;yield return null;double previewRender=Capture(camera,"codex_반투명_수령전.png");costs.Add(new{state="previewCameraSubmitCPU",ms=previewRender,note="UnityStats reports final blit only; scene draw calls not measured"});
            body.position=session.ActiveOrder.PickupPoint.position;session.Player.position=body.position;Physics.SyncTransforms();Check(session.TryPickup(),"real pickup");
            body.position=duck.transform.position+Vector3.up;session.Player.position=body.position;Physics.SyncTransforms();Call(planner,"OnSessionChanged");
            body.position=center+Vector3.up*50;session.Player.position=body.position;
            Check(duck.StrikeAllowed&&duck.RuntimeActive&&!Preview(duck),"pickup immediately enables authority and physics");
            Check((float)Prop(duck,"GhostOpacity")<1f,"pickup starts short visual fade");
            float until=Time.time+.4f;while(Time.time<until){Call(planner,"ApplyPendingSlots");yield return null;}
            Check(Mathf.Approximately((float)Prop(duck,"GhostOpacity"),1f),"fade reaches full opacity");double opaqueRender=Capture(camera,"codex_반투명_수령후.png");costs.Add(new{state="opaqueCameraSubmitCPU",ms=opaqueRender});
            Check(session.TryLoseFood(null),"food recovery starts");Call(planner,"OnSessionChanged");Check(duck.StrikeAllowed&&duck.RuntimeActive&&(float)Prop(duck,"GhostOpacity")==1f,"recovery keeps opaque active hazard");
            body.position=session.ActiveOrder.PickupPoint.position;session.Player.position=body.position;Physics.SyncTransforms();Check(session.TryPickup(),"replacement pickup");
            Check(session.TryDeliver(session.ActiveOrder.Recipient),"delivery completes");Call(planner,"OnSessionChanged");Check(all.Where(s=>s.Role==HazardRole.Trigger).All(s=>!s.StrikeAllowed&&!Preview(s)&&!s.RuntimeActive),"call completion clears all previews and authority");
            Check(session.OpenCall()&&session.TryAcceptOrder(session.Candidates[0]),"next call");Call(planner,"OnSessionChanged");Check(planner.Slots.Select(s=>s.GetInstanceID()).SequenceEqual(ids),"slot pool survives call transition");
            foreach(var pair in materials){var material=Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m=>m.GetInstanceID()==pair.Key);Check(material!=null&&EditorJsonUtility.ToJson(material)==pair.Value,"shared material unchanged "+pair.Key);}
        }
        private static double Capture(Camera camera,string filename)
        {
            var texture=new RenderTexture(1920,1080,24,RenderTextureFormat.ARGB32);texture.Create();
            var previous=RenderTexture.active;
            try
            {
                var request=new UniversalRenderPipeline.SingleCameraRequest{destination=texture};
                RenderPipeline.SubmitRenderRequest(camera,request);var timer=Stopwatch.StartNew();
                for(int sample=0;sample<5;sample++)RenderPipeline.SubmitRenderRequest(camera,request);
                timer.Stop();double ms=timer.Elapsed.TotalMilliseconds/5;RenderTexture.active=texture;
                var image=new Texture2D(texture.width,texture.height,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,texture.width,texture.height),0,0);image.Apply();
                File.WriteAllBytes(Path.Combine(MeasureRunner.Arg("-previewDirectory",@"C:\UnityWork\ge\공유\결과"),filename),image.EncodeToPNG());Destroy(image);return ms;
            }
            finally{RenderTexture.active=previous;texture.Release();Destroy(texture);}
        }
        private void Save()=>File.WriteAllText(MeasureRunner.Arg("-previewMetrics",@"C:\UnityWork\ge\공유\결과\codex_preview_metrics.json"),JsonConvert.SerializeObject(costs,Formatting.Indented));
        private static bool Preview(HazardSlot slot)=>(bool)Prop(slot,"IsPreviewVisible");
        private static object Prop(object o,string n)=>o.GetType().GetProperty(n,Any).GetValue(o);
        private static object Get(object o,string n)=>o.GetType().GetField(n,Any).GetValue(o);
        private static void Set(object o,string n,object v)=>o.GetType().GetField(n,Any).SetValue(o,v);
        private static void Property(object o,string n,object v)=>o.GetType().GetProperty(n,Any).SetValue(o,v);
        private static object Call(object o,string n)=>o.GetType().GetMethod(n,Any).Invoke(o,null);
        private static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    }
}
