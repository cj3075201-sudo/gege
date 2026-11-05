using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeliveryRider.Editor
{
    [InitializeOnLoad]
    public static class MapBoundaryPlayChecks
    {
        private const string Pending="MapBoundary.Pending";
        static MapBoundaryPlayChecks()=>EditorApplication.playModeStateChanged+=Mode;
        public static void Run(){EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);SessionState.SetBool(Pending,true);EditorApplication.EnterPlaymode();}
        private static void Mode(PlayModeStateChange mode)
        {
            if(!SessionState.GetBool(Pending,false))return;
            if(mode==PlayModeStateChange.EnteredPlayMode)new GameObject("MapBoundaryProbe").AddComponent<MapBoundaryProbe>();
            if(mode==PlayModeStateChange.EnteredEditMode){SessionState.SetBool(Pending,false);EditorApplication.Exit(SessionState.GetInt(Pending+".exit",1));}
        }
        internal static void Finish(bool pass){Debug.Log(pass?"MAP_BOUNDARY_ALL_PASS":"MAP_BOUNDARY_FAILED");SessionState.SetInt(Pending+".exit",pass?0:1);EditorApplication.ExitPlaymode();}
    }
    public sealed class MapBoundaryProbe:MonoBehaviour
    {
        private const BindingFlags Any=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        private IEnumerator probe;private readonly List<object> rows=new List<object>();private bool success=true;
        private void Start()=>probe=Probe();
        private void Update(){try{if(probe!=null&&!probe.MoveNext()){probe=null;Save();MapBoundaryPlayChecks.Finish(success);}}catch(Exception e){Debug.LogError(e);probe=null;Save();MapBoundaryPlayChecks.Finish(false);}}
        private IEnumerator Probe()
        {
            Time.captureDeltaTime=.02f;yield return null;
            var session=FindFirstObjectByType<DeliverySession>();var motor=session.Player.GetComponent<RiderMotor>();var body=motor.GetComponent<Rigidbody>();var planner=FindFirstObjectByType<HazardPlanner>();
            Property(session,"Progress",new RunProgress(4,100000));Set(session,"openZones",new List<string>{"C","A","B","D"});
            foreach(var barrier in FindObjectsByType<ZoneBarrier>(FindObjectsSortMode.None))barrier.Refresh();
            planner.enabled=false;foreach(var slot in planner.Slots)slot.SetRuntimeActive(false);
            foreach(string name in new[]{"Traffic","Pedestrians"}){var root=GameObject.Find(name);if(root!=null)root.SetActive(false);}
            motor.SetInputBlocked(true);
            var ground=(Collider[])typeof(RoadNetwork).GetField("ground",Any).GetValue(FindFirstObjectByType<RoadNetwork>());var bounds=ground[0].bounds;
            var cases=new[]{(name:"recorded_manhole_north",start:new Vector3(192.0119f,.86f,35.2096f),velocity:new Vector3(-.01907586f,24,20.499992f)),(name:"hydrant_north",start:new Vector3(192,.86f,77),velocity:new Vector3(0,30,7)),(name:"hydrant_south",start:new Vector3(-50,.86f,-142),velocity:new Vector3(0,30,-7)),(name:"hydrant_west",start:new Vector3(-82.2f,.86f,-98.7f),velocity:new Vector3(-7,30,0)),(name:"hydrant_east",start:new Vector3(192,.86f,49),velocity:new Vector3(7,30,0))};
            foreach(var sample in cases)
            {
                Call(motor,"ResetReaction");Set(motor,"safeRecoveryGuardUntil",0f);Set(motor,"nextTrafficHitAt",0f);Set(motor,"lastHazard",null);Set(motor,"lastHazardHitAt",float.NegativeInfinity);
                body.position=sample.start;motor.transform.position=sample.start;body.linearVelocity=Vector3.zero;body.angularVelocity=Vector3.zero;Physics.SyncTransforms();
                int restores=0;Action restored=()=>restores++;motor.SafePositionRestored+=restored;
                var source=new GameObject("BoundaryLaunchSource");bool accepted=motor.TryHazardCrash(source,sample.velocity,.6f);bool outside=false;float maxY=body.position.y;float until=Time.time+6f;
                try
                {
                    while(Time.time<until)
                    {
                        outside|=!BelowGroundChecks.Inside(bounds,body.position);maxY=Mathf.Max(maxY,body.position.y);
                        if(outside||restores>0)break;yield return null;
                    }
                    bool pass=accepted&&!outside&&restores==0;success&=pass;rows.Add(new{sample.name,pass,accepted,outside,restores,maxY,end=BelowGroundChecks.V(body.position)});
                    Debug.Log($"MAP_BOUNDARY_{(pass?"PASS":"FAIL")} {sample.name} outside={outside} restores={restores} maxY={maxY:F3}");
                }
                finally{motor.SafePositionRestored-=restored;Destroy(source);}
            }
        }
        private void Save()=>File.WriteAllText(MeasureRunner.Arg("-boundaryOut",@"C:\UnityWork\ge\공유\결과\codex_below_boundary_play.json"),JsonConvert.SerializeObject(rows,Formatting.Indented));
        private static void Set(object o,string n,object v)=>o.GetType().GetField(n,Any).SetValue(o,v);
        private static void Property(object o,string n,object v)=>o.GetType().GetProperty(n,Any).SetValue(o,v);
        private static void Call(object o,string n)=>o.GetType().GetMethod(n,Any).Invoke(o,null);
    }
}
