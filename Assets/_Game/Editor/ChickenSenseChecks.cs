using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeliveryRider.Editor
{
    // Real-map check of when an idle chicken flock reacts: before pickup, then just inside and outside its radius.
    [InitializeOnLoad]
    public static class ChickenSenseChecks
    {
        private const string Pending="ChickenSense.Pending";
        static ChickenSenseChecks()=>EditorApplication.playModeStateChanged+=OnMode;
        public static void Run()
        {
            EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);
            SessionState.SetBool(Pending,true);EditorApplication.EnterPlaymode();
        }
        private static void OnMode(PlayModeStateChange mode)
        {
            if(!SessionState.GetBool(Pending,false))return;
            if(mode==PlayModeStateChange.EnteredPlayMode)new GameObject("ChickenSenseProbe").AddComponent<ChickenSenseProbe>();
            if(mode==PlayModeStateChange.EnteredEditMode){SessionState.SetBool(Pending,false);EditorApplication.Exit(SessionState.GetInt(Pending+".exit",1));}
        }
        internal static void Finish(Exception e)
        {
            Debug.Log(e==null?"CHICKEN_SENSE_PASS":"CHICKEN_SENSE_FAIL "+e);
            SessionState.SetInt(Pending+".exit",e==null?0:1);EditorApplication.ExitPlaymode();
        }
    }
    public sealed class ChickenSenseProbe:MonoBehaviour
    {
        private const BindingFlags Any=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private IEnumerator probe;
        private void Start()=>probe=Probe();
        private void Update(){try{if(probe!=null&&!probe.MoveNext()){probe=null;ChickenSenseChecks.Finish(null);}}catch(Exception e){probe=null;ChickenSenseChecks.Finish(e);}}
        private static object Get(object o,string n)=>o.GetType().GetField(n,Any).GetValue(o);
        private static void Set(object o,string n,object v)=>o.GetType().GetField(n,Any).SetValue(o,v);
        private IEnumerator Probe()
        {
            yield return null;yield return null;
            var session=FindFirstObjectByType<DeliverySession>();var planner=FindFirstObjectByType<HazardPlanner>();
            var rider=session.Player.GetComponent<RiderMotor>();var body=rider.GetComponent<Rigidbody>();
            rider.GetType().GetMethod("ResetReaction",Any).Invoke(rider,null);rider.enabled=false;body.interpolation=RigidbodyInterpolation.None;body.isKinematic=true;rider.SetInputBlocked(true);Set(rider,"nextZoneCheckAt",1e9f);
            var recovery=rider.GetComponent<RiderSafeRecovery>();if(recovery!=null)recovery.enabled=false;
            void Move(Vector3 p){body.position=p;session.Player.position=p;Physics.SyncTransforms();}
            var far=new Vector3(1000,30,1000);Move(far);
            if(!session.OpenCall()||!session.TryAcceptOrder(session.Candidates[0]))throw new InvalidOperationException("call not accepted");
            yield return null;
            var slot=planner.Slots.FirstOrDefault(s=>s.Type=="chicken"&&s.Selected)??planner.Slots.First(s=>s.Type=="chicken");
            planner.GetType().GetMethod("Select",Any).Invoke(planner,new object[]{slot});
            var flock=slot.GetComponent<ChaseFlockHazard>();
            float radius=(float)Get(flock,"triggerRadius")*slot.TriggerScale;
            var side=slot.transform.right;var centre=slot.transform.position+Vector3.up;
            void Report(string name)=>Debug.Log($"CHICKEN_SENSE {name}: stage={session.Stage} preview={slot.IsPreviewVisible} active={slot.RuntimeActive} strike={slot.StrikeAllowed} enabled={flock.enabled} phase={Get(flock,"phase")} birdVisible={flock.GetComponentsInChildren<Renderer>().Any(r=>r.enabled)} radius={radius} paused={session.IsPaused} settled={session.IsSettled} map={session.IsMapOpen} recovering={rider.IsRecovering} riderField={(Get(flock,"rider")!=null)} riderPos={rider.transform.position} bodyPos={body.position} slotPos={slot.transform.position} rootActive={slot.gameObject.activeInHierarchy} timeScale={Time.timeScale} fixedDt={Time.fixedDeltaTime} scale={slot.TriggerScale}");
            {float until=Time.time+.2f;while(Time.time<until)yield return null;}Report("before pickup, far");
            Move(centre+side*1f);{float until=Time.time+.5f;while(Time.time<until)yield return null;}Report("before pickup, 1m");
            string before=Get(flock,"phase").ToString();if(before=="Chasing")throw new InvalidOperationException("reacted before pickup");
            Move(far);yield return null;
            Move(session.ActiveOrder.PickupPoint.position);if(!session.TryPickup())throw new InvalidOperationException("pickup failed");
            Move(far);{float until=Time.time+.6f;while(Time.time<until)yield return null;}Report("carrying, far");
            Move(centre+side*(radius+.5f));{float until=Time.time+.5f;while(Time.time<until)yield return null;}Report("carrying, radius+.5m");
            if(Get(flock,"phase").ToString()!="Idle")throw new InvalidOperationException("reacted outside radius");
            Move(centre+side*(radius-.5f));{float until=Time.time+.5f;while(Time.time<until)yield return null;}Report("carrying, radius-.5m");
            if(Get(flock,"phase").ToString()!="Chasing")throw new InvalidOperationException("did not react inside radius");
        }
    }
}
