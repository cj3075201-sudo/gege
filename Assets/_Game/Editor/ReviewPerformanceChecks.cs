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
using Debug = UnityEngine.Debug;

namespace DeliveryRider.Editor
{
    [InitializeOnLoad]
    public static class ReviewPerformanceChecks
    {
        private const string Pending="HtmlReview.Performance";
        static ReviewPerformanceChecks()=>EditorApplication.playModeStateChanged+=OnMode;
        public static void Run()
        {
            EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);
            SessionState.SetBool(Pending,true);EditorApplication.EnterPlaymode();
        }
        private static void OnMode(PlayModeStateChange mode)
        {
            if(!SessionState.GetBool(Pending,false))return;
            if(mode==PlayModeStateChange.EnteredPlayMode)new GameObject("ReviewPerformanceProbe").AddComponent<ReviewPerformanceProbe>();
            if(mode==PlayModeStateChange.EnteredEditMode){SessionState.SetBool(Pending,false);EditorApplication.Exit(SessionState.GetInt(Pending+".exit",1));}
        }
        internal static void Finish(Exception e)
        {
            if(e!=null)Debug.LogError("REVIEW_PERF_FAIL "+e);else Debug.Log("REVIEW_PERF_PASS");
            SessionState.SetInt(Pending+".exit",e==null?0:1);EditorApplication.ExitPlaymode();
        }
    }
    public sealed class ReviewPerformanceProbe:MonoBehaviour
    {
        private const BindingFlags Any=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private IEnumerator probe;
        private void Start()=>probe=Probe();
        private void Update()
        {try{if(probe!=null&&!probe.MoveNext()){probe=null;ReviewPerformanceChecks.Finish(null);}}catch(Exception e){probe=null;ReviewPerformanceChecks.Finish(e);}}
        private IEnumerator Probe()
        {
            yield return null;
            var session=FindFirstObjectByType<DeliverySession>();var planner=FindFirstObjectByType<HazardPlanner>();
            var rider=session.Player.GetComponent<RiderMotor>();rider.GetComponent<Rigidbody>().isKinematic=true;
            Set(planner,"randomEachRun",false);Set(planner,"visibilityHoldRadius",0f);
            var rows=new List<object>();
            foreach(var sample in new[]{(day:1,scale:1f),(day:4,scale:1f),(day:4,scale:5f)})
            {
                Property(session,"Progress",new RunProgress(sample.day,100000));Set(session,"openZones",session.Economy.ZonesForDay(sample.day));
                Property(session,"DayPhase",WorkDayPhase.Working);Set(session,"dayDuration",1e9d);Property(session,"Stage",DeliveryStage.Available);Property(session,"ActiveOrder",null);Property(session,"IsCallOpen",false);Property(session,"CurrentZone","C");
                ((IList)Get(session,"candidates")).Clear();Call(session,"Notify");
                Set(planner,"day",sample.day);Set(planner,"seed",111);Set(planner,"hazardCountScale",sample.scale);
                var timer=Stopwatch.StartNew();planner.Respawn();timer.Stop();double spawnMs=timer.Elapsed.TotalMilliseconds;
                yield return null;
                if(!session.OpenCall()||!session.TryAcceptOrder(session.Candidates[0]))throw new Exception("accept failed");
                var pickup=session.ActiveOrder.PickupPoint.position;session.Player.position=pickup;Physics.SyncTransforms();
                timer.Restart();if(!session.TryPickup())throw new Exception("pickup failed");timer.Stop();double pickupMs=timer.Elapsed.TotalMilliseconds;
                timer.Restart();if(!session.TryDeliver(session.ActiveOrder.Recipient))throw new Exception("deliver failed");timer.Stop();
                var row=new{sample.day,sample.scale,spawnMs,pickupMs,completeMs=timer.Elapsed.TotalMilliseconds,slots=planner.Slots.Count,requested=planner.RequestedCounts.ToDictionary(p=>p.Key,p=>p.Value)};rows.Add(row);
                Debug.Log("REVIEW_PERF "+JsonConvert.SerializeObject(row));yield return null;
            }
            var output=MeasureRunner.Arg("-reviewOut","Library/html_review_performance.json");File.WriteAllText(output,JsonConvert.SerializeObject(rows,Formatting.Indented));
        }
        private static object Get(object o,string n)=>o.GetType().GetField(n,Any).GetValue(o);
        private static void Set(object o,string n,object v)=>o.GetType().GetField(n,Any).SetValue(o,v);
        private static void Property(object o,string n,object v)=>o.GetType().GetProperty(n,Any).SetValue(o,v);
        private static void Call(object o,string n)=>o.GetType().GetMethod(n,Any).Invoke(o,null);
    }
}
