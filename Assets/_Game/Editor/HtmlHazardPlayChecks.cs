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
using Unity.Profiling;

namespace DeliveryRider.Editor
{
    [InitializeOnLoad]
    public static class HtmlHazardPlayChecks
    {
        private const string Pending = "HtmlHazardPlayChecks.Pending";
        static HtmlHazardPlayChecks() => EditorApplication.playModeStateChanged += Mode;
        public static void Run()
        {
            EditorSceneManager.OpenScene("Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity", OpenSceneMode.Single);
            SessionState.SetBool(Pending,true); SessionState.SetInt(Pending+".exit",1); EditorApplication.EnterPlaymode();
        }
        private static void Mode(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(Pending,false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) new GameObject("HTML rules measurement").AddComponent<HtmlHazardMeasurement>();
            if (state == PlayModeStateChange.EnteredEditMode) { SessionState.SetBool(Pending,false); EditorApplication.Exit(SessionState.GetInt(Pending+".exit",1)); }
        }
        public static void Finish(Exception error)
        {
            if (error != null) Debug.LogError("HTML_PLAY_FAIL " + error);
            else Debug.Log("HTML_PLAY_PASS: actual Myeongji scene, 4 days x 3 seeds x 4 accepted calls, stage/reset/count/distribution/spacing/zone checks");
            SessionState.SetInt(Pending+".exit",error == null?0:1); EditorApplication.ExitPlaymode();
        }
    }
    public sealed class HtmlHazardMeasurement : MonoBehaviour
    {
        private const BindingFlags Any = BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private IEnumerator probe;
        private readonly List<object> rows = new List<object>();
        private readonly Dictionary<string,int> failures = new Dictionary<string,int>();
        private void Start() { probe = Probe(); }
        private void Update()
        {
            if (probe == null) return;
            try { if (probe.MoveNext()) return; probe=null; HtmlHazardPlayChecks.Finish(null); }
            catch(Exception error) { probe=null; Save(); HtmlHazardPlayChecks.Finish(error); }
        }
        private IEnumerator Probe()
        {
            yield return null;
            var session=FindFirstObjectByType<DeliverySession>(); var planner=FindFirstObjectByType<HazardPlanner>();
            var rider=session.Player.GetComponent<RiderMotor>(); var body=rider.GetComponent<Rigidbody>();
            body.isKinematic=true;
            Set(planner,"visibilityHoldRadius",0f); Set(planner,"randomEachRun",false);
            for(int day=1;day<=4;day++) for(int seed=1;seed<=3;seed++)
            {
                Property(session,"Progress",new RunProgress(day,100000)); Set(session,"openZones",session.Economy.ZonesForDay(day));
                Property(session,"DayPhase",WorkDayPhase.Working); Set(session,"dayDuration",1e9d);
                Property(session,"ActiveOrder",null); Property(session,"Stage",DeliveryStage.Available); Property(session,"IsCallOpen",false);
                Property(session,"CurrentZone","C"); ((IList)Get(session,"candidates")).Clear(); Call(session,"Notify");
                Set(planner,"day",day); Set(planner,"seed",day*1009+seed); Set(planner,"callRandom",null);
                var watch=System.Diagnostics.Stopwatch.StartNew(); planner.Respawn(); watch.Stop();
                yield return null;
                var open=new HazardZoneFilter(session.ZoneMap,session.IsZoneOpen);
                var eligible = (List<HazardCandidate>)Get(planner,"availableCandidates");
                File.WriteAllText("Library/html_layout_debug.json",JsonConvert.SerializeObject(new {day,seed,candidates=eligible.Select(c=>new{c.type,c.alley,x=c.position.x,z=c.position.z}),slots=planner.Slots.Select(s=>new{s.Type,s.Combo,s.Candidate.alley,x=s.transform.position.x,z=s.transform.position.z}),requested=planner.RequestedCounts},Formatting.Indented));
                File.Copy("Library/html_layout_debug.json",$"Library/html_layout_day{day}_seed{seed}.json",true);
                var all=planner.Slots.ToArray(); var ids=all.Select(s=>s.GetInstanceID()).ToArray();
                Check(all.All(s=>open.PointOpen(s.transform.position)),"closed-zone placement");
                var objects=all.Where(s=>s.Type=="manhole"||s.Type=="hydrant").ToArray();
                float minimum=float.PositiveInfinity;
                for(int a=0;a<objects.Length;a++)for(int b=a+1;b<objects.Length;b++) minimum=Mathf.Min(minimum,Vector3.ProjectOnPlane(objects[a].transform.position-objects[b].transform.position,Vector3.up).magnitude);
                Check(minimum>=4.999f,"ground objects closer than 5 m: "+minimum);
                foreach(var pair in planner.RequestedCounts) Check(all.Count(s=>s.Type==pair.Key)==pair.Value,"daily requested count "+pair.Key+" placed="+all.Count(s=>s.Type==pair.Key)+" requested="+pair.Value);
                for(int call=0;call<4;call++)
                {
                    Check(session.OpenCall(),"call opens"); var acceptance=MeasureAccept(session); Check(acceptance.ok,"call accepted");
                    Check(planner.SelectedTriggers==new[]{5,8,10,13}[day-1],"ACT day="+day+" selected="+planner.SelectedTriggers);
                    Check(all.Where(s=>s.Role==HazardRole.Trigger).All(s=>!s.StrikeAllowed),"pre-pickup gate");
                    var selected=all.Where(s=>s.Selected&&s.Type!="celebrity").ToArray();
                    for(int a=0;a<selected.Length;a++)for(int b=a+1;b<selected.Length;b++)
                    {
                        float gap=selected[a].Combo>=0&&selected[a].Combo==selected[b].Combo?2.9f:9f;
                        Check(Vector3.ProjectOnPlane(selected[a].transform.position-selected[b].transform.position,Vector3.up).sqrMagnitude>=gap*gap-.001f,"active trigger spacing");
                    }
                    int cap=Mathf.Max(2,Mathf.CeilToInt(selected.Length*.25f));
                    Check(selected.GroupBy(s=>s.Type).All(g=>g.Count()<=cap),"type cap");
                    Check(selected.Count(s=>s.Type=="duck")<=1,"one duck");
                    Check(selected.Count(s=>s.Type=="theft"||s.Type=="clone")<=1,"single stealing trigger");
                    body.position=session.ActiveOrder.PickupPoint.position; session.Player.position=body.position; Physics.SyncTransforms();
                    Check(session.TryPickup(),$"pickup move={session.CanMove} stage={session.Stage} range={session.TargetDistance} recovering={rider.IsRecovering} map={session.IsMapOpen}");
                    Check(selected.All(s=>s.StrikeAllowed&&s.RuntimeActive),"selected delivery slots enabled");
                    Check(session.TryLoseFood(null),"food loss enters recovery");
                    Check(selected.All(s=>s.StrikeAllowed),"recovery remains armed");
                    Check(session.TryPickup(),"replacement pickup");
                    rows.Add(new {day,seed,call,selected=planner.SelectedTriggers,near=planner.NearSelectedTriggers,nearRatio=(float)planner.NearSelectedTriggers/planner.SelectedTriggers,
                        counts=selected.GroupBy(s=>s.Type).ToDictionary(g=>g.Key,g=>g.Count()),placed=all.GroupBy(s=>s.Type).ToDictionary(g=>g.Key,g=>g.Count()),
                        requested=planner.RequestedCounts,combos=planner.PlacedCombos,minimumObjectGap=minimum,rollMs=planner.LastRollMilliseconds,rollBytes=planner.LastRollAllocatedBytes,rollAllocationUnit=planner.LastRollAllocationUnit,rollHeapDelta=planner.LastRollHeapDeltaBytes,
                        acceptMs=acceptance.ms,acceptBytes=acceptance.bytes,acceptHeapDelta=acceptance.net,acceptCollections=acceptance.collections,
                        layoutMs=watch.Elapsed.TotalMilliseconds,checksBefore=all.Length,checksAfter=all.Count(s=>s.RuntimeActive),closed=all.Count(s=>!open.PointOpen(s.transform.position))});
                    Check(session.TryDeliver(session.ActiveOrder.Recipient),"complete");
                    Check(planner.SelectedTriggers==0&&all.Where(s=>s.Role==HazardRole.Trigger).All(s=>!s.StrikeAllowed&&!s.RuntimeActive),"completion clears selection and gate");
                    Check(planner.Slots.Select(s=>s.GetInstanceID()).SequenceEqual(ids),"call creates or destroys slots");
                    yield return null;
                }
                Debug.Log($"HTML_PLAY day={day} seed={seed} placed={all.Length} combos={planner.PlacedCombos} gap={minimum:F2} layoutMs={watch.Elapsed.TotalMilliseconds:F2}");
                Save();
            }
            // Every trigger type, same object and components, spent -> pause -> new call -> working again.
            planner.enabled=false; rider.enabled=false; body.position=new Vector3(1000f,1f,1000f);session.Player.position=body.position;
            var original=session.LastCompletedOrder;
            foreach(var slot in planner.Slots.Where(s=>s.Role==HazardRole.Trigger).GroupBy(s=>s.Type).Select(g=>g.First()))
            {
                var part=slot.GetComponents<MonoBehaviour>().First(c=>c is IPlacedHazard);
                for(int cycle=0;cycle<2;cycle++)
                {
                    var phaseField=part.GetType().GetField("phase",Any);
                    if(phaseField!=null&&Enum.GetNames(phaseField.FieldType).Contains("Spent"))phaseField.SetValue(part,Enum.Parse(phaseField.FieldType,"Spent"));
                    slot.SetRuntimeActive(false);
                    Property(session,"ActiveOrder",new DeliveryOrderOffer(10000+cycle,original.PickupPoint,original.Recipient,original.DestinationPoint,"probe","probe","probe",1,1,100,100,"probe","C","A",0,0));
                    Property(session,"Stage",DeliveryStage.Carrying);slot.StrikeAllowed=true;slot.SetRuntimeActive(true,true);
                    yield return null;yield return null;
                    Check(part.enabled&&slot.RuntimeActive,"controller re-enabled "+slot.Type);
                    if(phaseField!=null)Check(phaseField.GetValue(part).ToString()!="Spent","spent state reset "+slot.Type);
                    if(part is RollingTireHazard tire)Check(tire.TryActivate(),"tire activates after reset");
                    if(part is CelebrityCrowdHazard)Check(!(bool)Get(part,"appeared"),"celebrity rearmed after reset");
                    slot.SetRuntimeActive(false);
                }
                Debug.Log("HTML_REARM_PLAY_PASS type="+slot.Type+" cycles=2 sameRoot="+slot.GetInstanceID());
            }
            Save();
        }
        private static (bool ok,double ms,long bytes,long net,int collections) MeasureAccept(DeliverySession session)
        {
            using var recorder=ProfilerRecorder.StartNew(ProfilerCategory.Memory,"GC.Alloc",1,ProfilerRecorderOptions.StartImmediately|ProfilerRecorderOptions.SumAllSamplesInFrame|ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            long before=UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();int collections=GC.CollectionCount(0);
            var watch=System.Diagnostics.Stopwatch.StartNew();bool ok=session.TryAcceptOrder(session.Candidates[0]);watch.Stop();
            return(ok,watch.Elapsed.TotalMilliseconds,recorder.Valid&&recorder.UnitType.ToString()=="Bytes"?recorder.CurrentValue:-1,UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong()-before,GC.CollectionCount(0)-collections);
        }
        private void Check(bool success,string message) { if(!success)throw new InvalidOperationException(message); }
        private void Save() => File.WriteAllText("Library/html_play_metrics.json",JsonConvert.SerializeObject(new {rows,failures},Formatting.Indented));
        private static object Get(object owner,string name)=>owner.GetType().GetField(name,Any).GetValue(owner);
        private static void Set(object owner,string name,object value)=>owner.GetType().GetField(name,Any).SetValue(owner,value);
        private static void Property(object owner,string name,object value)=>owner.GetType().GetProperty(name,Any).SetValue(owner,value);
        private static object Call(object owner,string name)=>owner.GetType().GetMethod(name,Any).Invoke(owner,null);
    }
}
