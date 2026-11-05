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
using Debug=UnityEngine.Debug;

namespace DeliveryRider.Editor
{
    [InitializeOnLoad]
    public static class ReviewRuntimePlayChecks
    {
        private const string Pending="ReviewRuntime.Pending";
        static ReviewRuntimePlayChecks()=>EditorApplication.playModeStateChanged+=OnMode;
        public static void Run(){EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);SessionState.SetBool(Pending,true);EditorApplication.EnterPlaymode();}
        private static void OnMode(PlayModeStateChange mode)
        {
            if(!SessionState.GetBool(Pending,false))return;
            if(mode==PlayModeStateChange.EnteredPlayMode)new GameObject("ReviewRuntimeProbe").AddComponent<ReviewRuntimeProbe>();
            if(mode==PlayModeStateChange.EnteredEditMode){SessionState.SetBool(Pending,false);EditorApplication.Exit(SessionState.GetInt(Pending+".exit",1));}
        }
        internal static void Finish(bool success){Debug.Log(success?"REVIEW_RUNTIME_ALL_PASS":"REVIEW_RUNTIME_FAILED");SessionState.SetInt(Pending+".exit",success?0:1);EditorApplication.ExitPlaymode();}
    }
    public sealed class ReviewRuntimeProbe:MonoBehaviour
    {
        private const BindingFlags Any=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        private DeliverySession session;private HazardPlanner planner;private RiderMotor rider;private Rigidbody body;
        private IEnumerator probe;private readonly List<string> failures=new List<string>();private readonly List<object> measurements=new List<object>();
        private void Start()=>probe=Probe();
        private void Update(){try{if(probe!=null&&!probe.MoveNext()){probe=null;Save();ReviewRuntimePlayChecks.Finish(failures.Count==0);}}catch(Exception e){Debug.LogError(e);probe=null;failures.Add(e.ToString());Save();ReviewRuntimePlayChecks.Finish(false);}}
        private IEnumerator Probe()
        {
            yield return null;
            session=FindFirstObjectByType<DeliverySession>();planner=FindFirstObjectByType<HazardPlanner>();rider=session.Player.GetComponent<RiderMotor>();body=rider.GetComponent<Rigidbody>();body.isKinematic=true;body.interpolation=RigidbodyInterpolation.None;
            rider.enabled=false;Call(rider,"ResetReaction");
            rider.SetInputBlocked(true);Set(rider,"nextZoneCheckAt",1e9f);
            var recovery=rider.GetComponent<RiderSafeRecovery>();if(recovery!=null)recovery.enabled=false;
            Property(session,"Progress",new RunProgress(4,100000));Set(session,"openZones",session.Economy.ZonesForDay(4));Set(planner,"day",4);Set(planner,"randomEachRun",false);Set(planner,"visibilityHoldRadius",0f);planner.Respawn();yield return null;
            session.OpenCall();session.TryAcceptOrder(session.Candidates[0]);session.Player.position=session.ActiveOrder.PickupPoint.position;Physics.SyncTransforms();Check(session.TryPickup(),"pickup setup");
            planner.enabled=false;
            foreach(var slot in planner.Slots){slot.Selected=false;slot.NeedsCallReset=false;slot.SetRuntimeActive(false);}
            body.position=new Vector3(1000,1,1000);session.Player.position=body.position;
            var cases=new Dictionary<string,Func<IEnumerator>>{{"M5",DogReset},{"L1",ClonePause},{"L2",PendingReentry},{"L3",ConsumedFood},{"M4",CrowdPause},{"L4",CallReproduction},{"M2",F1Counts},{"SingleUse",SingleUse},{"SnatchOnce",SnatchOnce},{"MuscleOnce",MuscleOnce}};
            foreach(var pair in cases)
            {
                if(!MeasureRunner.Arg("-reviewCases",string.Join(",",cases.Keys)).Split(',').Contains(pair.Key))continue;
                IEnumerator test=pair.Value();bool failed=false;
                while(true)
                {
                    bool next;object current=null;
                    try{next=test.MoveNext();if(next)current=test.Current;}
                    catch(Exception e){failed=true;failures.Add(pair.Key+": "+e);Debug.LogError("REVIEW_RUNTIME_FAIL "+pair.Key+": "+e);break;}
                    if(!next)break;yield return current;
                }
                if(!failed)Debug.Log("REVIEW_RUNTIME_PASS "+pair.Key);
                planner.enabled=false;foreach(var slot in planner.Slots){slot.Selected=false;slot.NeedsCallReset=false;slot.SetRuntimeActive(false);}
                body.position=new Vector3(1000,1,1000);session.Player.position=body.position;
                Property(session,"Stage",DeliveryStage.Carrying);yield return null;
            }
        }
        private IEnumerator DogReset()
        {
            var slot=Slot("dog");slot.Selected=true;slot.StrikeAllowed=true;slot.SetRuntimeActive(true,true);var dog=(Transform)Get(slot.GetComponent<DogHazard>(),"dog");var home=(Vector3)Get(slot.GetComponent<DogHazard>(),"home");
            dog.localPosition=home+Vector3.forward*75;slot.SetRuntimeActive(false);slot.SetRuntimeActive(true,true);
            Check(Vector3.Distance(dog.localPosition,home)<.001f,"reactivated dog must return to its baked home");yield break;
        }
        private IEnumerator ClonePause()
        {
            var slot=Slot("clone");slot.Selected=true;slot.StrikeAllowed=true;slot.SetRuntimeActive(true,true);var part=slot.GetComponent<CloneRiderHazard>();var clones=(FoodThief[])Get(part,"clones");
            foreach(var clone in clones)clone.gameObject.SetActive(true);var text=(TMPro.TMP_Text)Get(part,"callout");text.gameObject.SetActive(true);slot.PauseKeepingVisuals();
            Check(clones.All(c=>!c.gameObject.activeInHierarchy),"cancelled decoys must not remain as old-position ghosts");Check(!text.gameObject.activeInHierarchy,"cancelled clone !!! must be hidden");
            Check(slot.GetComponentsInChildren<Collider>(true).All(c=>!c.enabled),"paused clone has no active collision");yield break;
        }
        private IEnumerator PendingReentry()
        {
            var slot=Slot("clone");slot.Selected=true;slot.StrikeAllowed=true;slot.SetRuntimeActive(true,true);var part=slot.GetComponent<CloneRiderHazard>();var thief=((FoodThief[])Get(part,"clones"))[0];thief.gameObject.SetActive(true);Set(part,"thief",thief);
            Property(session,"Stage",DeliveryStage.Carrying);Check(session.TryLoseFood(thief.transform),"clone owns food");Set(planner,"rolledOrder",session.ActiveOrder);planner.enabled=true;
            slot.Selected=false;var pending=(IList)Get(planner,"pendingSlots");pending.Clear();pending.Add(slot);
            Call(planner,"ApplyPendingSlots");Check(!session.IsChasingFood,"disabling thief makes food recoverable at shop");Check(pending.Count==0,"reentrant pending drain finishes");yield break;
        }
        private IEnumerator ConsumedFood()
        {
            var slot=Slot("theft");slot.Selected=true;slot.StrikeAllowed=true;slot.SetRuntimeActive(true,true);var part=slot.GetComponent<PigeonSnatchHazard>();var thief=(FoodThief)Get(part,"pigeon");thief.gameObject.SetActive(true);
            Property(session,"Stage",DeliveryStage.Carrying);Check(session.TryLoseFood(thief.transform),"bird owns food");var phase=part.GetType().GetField("phase",Any);phase.SetValue(part,Enum.Parse(phase.FieldType,"Flying"));
            Check(planner.TryConsumeSlot(slot.transform.position,.1f,null),"pal consumes selected bird");Call(planner,"ApplyPendingSlots");
            Check(!session.IsChasingFood,"consumed invisible bird must release food to replacement pickup");yield break;
        }
        private IEnumerator CrowdPause()
        {
            var slot=Slot("celebrity");var part=slot.GetComponent<CelebrityCrowdHazard>();Set(part,"waitingFans",6);Set(part,"triggerRadius",.1f);slot.Selected=true;slot.StrikeAllowed=true;
            slot.SetRuntimeActive(true,true);yield return null;yield return null;
            Check(!((IEnumerable<TrafficPedestrian>)Get(part,"actors")).Any(a=>a!=null&&a.gameObject.activeSelf),"distant selected crowd has no active fans");
            body.position=slot.transform.position+Vector3.forward*10+Vector3.up;session.Player.position=body.position;yield return null;yield return null;
            var actors=((IEnumerable<TrafficPedestrian>)Get(part,"actors")).Where(a=>a!=null&&a.gameObject.activeSelf).ToArray();Check(actors.Length==6,"six waiting fans near slot");
            var masses=actors.Select(a=>a.GetComponent<Rigidbody>().mass).ToArray();
            var timer=Stopwatch.StartNew();slot.SetRuntimeActive(false);slot.SetRuntimeActive(true,true);Call(part,"Update");timer.Stop();double resetMs=timer.Elapsed.TotalMilliseconds;yield return null;
            actors=((IEnumerable<TrafficPedestrian>)Get(part,"actors")).Where(a=>a!=null&&a.gameObject.activeSelf).ToArray();
            timer.Restart();slot.PauseKeepingVisuals();timer.Stop();double pauseMs=timer.Elapsed.TotalMilliseconds;
            measurements.Add(new{check="M4",resetMs,pauseMs,actors=actors.Length});yield return null;
            Check(actors.All(a=>a!=null&&a.gameObject.activeSelf),"25m pause must preserve visible pooled fans through Destroy frame");
            Check(actors.All(a=>!a.enabled),"paused fans stop their frame movement");
            timer.Restart();slot.SetRuntimeActive(true,true);Call(part,"Update");timer.Stop();yield return null;
            var reused=((IEnumerable<TrafficPedestrian>)Get(part,"actors")).Where(a=>a!=null&&a.gameObject.activeSelf).ToArray();Check(reused.Length==6&&reused.Select(a=>a.GetInstanceID()).OrderBy(i=>i).SequenceEqual(actors.Select(a=>a.GetInstanceID()).OrderBy(i=>i)),"crowd reset reuses actor pool: before="+string.Join(",",actors.Select(a=>a.GetInstanceID()))+" after="+string.Join(",",reused.Select(a=>a.GetInstanceID()))+" playerDistance="+Vector3.Distance(session.Player.position,slot.transform.position));
            Check(reused.Select(a=>a.GetComponent<Rigidbody>().mass).SequenceEqual(masses),"pooled fan mass does not multiply on each reset");
            measurements.Add(new{check="M4",pauseMs,resetMs=timer.Elapsed.TotalMilliseconds});
        }
        private IEnumerator CallReproduction()
        {
            Set(planner,"seed",111);Set(planner,"callRandom",new System.Random(99));planner.Respawn();
            string first=string.Join("|",planner.Slots.Where(s=>s.Selected).Select(s=>s.Type+":"+s.transform.position).OrderBy(s=>s));
            Set(planner,"callRandom",new System.Random(202));planner.Respawn();
            string second=string.Join("|",planner.Slots.Where(s=>s.Selected).Select(s=>s.Type+":"+s.transform.position).OrderBy(s=>s));
            Check(first==second,"Respawn must reproduce call draw from fixed seed regardless of previous RNG");yield break;
        }
        private IEnumerator F1Counts()
        {
            var settings=(HazardSpawnSettings)Get(planner,"settings");var tools=FindFirstObjectByType<DevTools>();Set(tools,"hazardSettings",settings);
            var target=(DevTarget)Call(tools,"HazardCounts");var traffic=FindFirstObjectByType<TrafficNetwork>();
            var rules=new DevTarget("Review call rules",planner,()=>new UnityEngine.Object[]{planner});var active=DevValue.Find(planner,"activePerDay.Array.data[4]","day5 ACT");rules.Add(new[]{active});
            var fleet=new DevTarget("Review traffic",traffic,()=>new UnityEngine.Object[]{traffic});var cars=DevValue.Find(traffic,"vehicleCountByDay.Array.data[4]","day5 cars");fleet.Add(new[]{cars});
            void Count(string type,string field,float value)
            {
                int index=settings.Kinds.ToList().FindIndex(k=>k.type==type);
                target.Set(target.Values.Single(v=>v.PropertyPath==$"kinds.Array.data[{index}].{field}.Array.data[4]"),value);
            }
            try
            {
                Count("falling","mainCountPerDay",2);Count("falling","alleyCountPerDay",3);
                Count("manhole","countPerDay",80);Count("hydrant","countPerDay",35);rules.Set(active,17);fleet.Set(cars,71);
                foreach(int day in new[]{5,6})
                {
                    Property(session,"Progress",new RunProgress(day,100000));Set(session,"openZones",session.Economy.ZonesForDay(day));Set(planner,"day",day);planner.Respawn();traffic.Respawn();
                    Check(planner.Slots.Count(s=>s.Type=="falling"&&!s.Candidate.alley)==2,"F1 main count actual day"+day);
                    Check(planner.Slots.Count(s=>s.Type=="falling"&&s.Candidate.alley)==3,"F1 alley count actual day"+day);
                    Check(planner.Slots.Count(s=>s.Type=="manhole")==80&&planner.Slots.Count(s=>s.Type=="hydrant")==35,"F1 object counts actual day"+day);
                    Check(planner.SelectedTriggers==17,"F1 ACT actual day"+day);Check(traffic.Cars.Count==71,"F1 fleet actual day"+day);
                }
                target.RevertAll();rules.RevertAll();fleet.RevertAll();planner.Respawn();traffic.Respawn();
                Check(planner.Slots.Count(s=>s.Type=="falling")==40&&planner.Slots.Count(s=>s.Type=="manhole")==84&&planner.Slots.Count(s=>s.Type=="hydrant")==38,"F1 reverted actual layout");
                Check(planner.SelectedTriggers==13&&traffic.Cars.Count==60,"F1 reverted ACT/fleet");
            }
            finally{target.RevertAll();rules.RevertAll();fleet.RevertAll();}
            yield break;
        }
        private IEnumerator SingleUse()
        {
            var duckSlot=Slot("duck");duckSlot.Selected=true;duckSlot.StrikeAllowed=true;duckSlot.SetRuntimeActive(true,true);
            var ducks=duckSlot.GetComponent<CrossingFlockHazard>();bool motorEnabled=rider.enabled;rider.enabled=false;Call(ducks,"Begin");Check(Get(ducks,"phase").ToString()=="Crossing","duck queue starts");
            float previousScale=Time.timeScale;Time.timeScale=10f;float until=Time.time+40f;
            try{while(Get(ducks,"phase").ToString()=="Crossing"&&Time.time<until)yield return null;}
            finally{Time.timeScale=previousScale;}
            Check(Get(ducks,"phase").ToString()=="Spent","duck stays spent after its queue clears");
            body.position=duckSlot.transform.position-duckSlot.transform.forward*40;session.Player.position=body.position;yield return null;yield return null;
            body.position=duckSlot.transform.position;session.Player.position=body.position;yield return null;yield return null;
            Check(Get(ducks,"phase").ToString()=="Spent","leaving and returning cannot restart ducks");
            duckSlot.SetRuntimeActive(false);duckSlot.SetRuntimeActive(true,true);Check(Get(ducks,"phase").ToString()=="Spent","same-call re-enable cannot restart ducks");
            duckSlot.SetRuntimeActive(false);
            var usedSlots=new List<HazardSlot>{duckSlot};
            foreach(string type in new[]{"chicken","tire_big","tire_small","melon"})
            {
                bool tireType=type.StartsWith("tire_");
                var slot=type=="tire_small"?planner.Slots.Where(s=>s.Type=="tire").Skip(1).First():Slot(tireType?"tire":type);
                var part=slot.GetComponents<MonoBehaviour>().First(c=>c is IPlacedHazard);
                if(tireType)Set(part,"largeChance",type=="tire_big"?1f:0f);
                slot.Selected=true;slot.StrikeAllowed=true;slot.SetRuntimeActive(true,true);usedSlots.Add(slot);
                body.position=slot.transform.position;session.Player.position=body.position;
                if(part is RollingTireHazard tire)
                {
                    Check((bool)Get(part,"waveLarge")== (type=="tire_big"),"explicit big/small tire wave");
                    Check(tire.TryActivate(),"tire starts");
                }
                else if(part is RollingMelonHazard)Call(part,"BeginWave");
                else Call(part,"FixedUpdate");
                Call(part,"Finish");Set(part,"elapsed",20f);body.position=new Vector3(1000,1,1000);session.Player.position=body.position;Call(part,"FixedUpdate");
                Check(Get(part,"phase").ToString()=="Spent",type+" cannot rearm after elapsed time or leaving range");slot.SetRuntimeActive(false);slot.SetRuntimeActive(true,true);
                Check(Get(part,"phase").ToString()=="Spent",type+" cannot restart by same-call component reset");slot.SetRuntimeActive(false);
            }
            var flockSlot=Slot("pigeonflock");flockSlot.Selected=true;flockSlot.StrikeAllowed=true;flockSlot.SetRuntimeActive(true,true);var flock=flockSlot.GetComponent<PigeonFlockHazard>();var shape=rider.GetComponent<Collider>();
            Call(flock,"TryContact",shape);Check((bool)Get(flock,"contactConsumed"),"flock contact consumed");Call(flock,"OnTriggerExit",shape);
            Check((bool)Get(flock,"contactConsumed"),"flock cannot rearm by leaving its volume");flockSlot.SetRuntimeActive(false);usedSlots.Add(flockSlot);
            foreach(string type in new[]{"manhole","hydrant"})
            {
                var slot=Slot(type);var part=slot.GetComponent<GroundContactHazard>();slot.SetRuntimeActive(true);Set(part,"phase",Enum.Parse(part.GetType().GetField("phase",Any).FieldType,"Spent"));
                Set(part,"nextArmedAt",Time.time+10f);Call(part,"FixedUpdate");Check(Get(part,"phase").ToString()=="Spent","object cooldown remains ten seconds");
                Set(part,"nextArmedAt",Time.time-.1f);Call(part,"FixedUpdate");Check(Get(part,"phase").ToString()=="Ready"&&!slot.StartedFor(session),"objects rearm independently of call usage");slot.SetRuntimeActive(false);
            }
            Check(session.TryDeliver(session.ActiveOrder.Recipient),"complete spent call");Check(session.OpenCall()&&session.TryAcceptOrder(session.Candidates[0]),"next call accepted");
            duckSlot.Selected=true;duckSlot.StrikeAllowed=true;duckSlot.SetRuntimeActive(true,true);Check(Get(ducks,"phase").ToString()=="Ready","new call rearms duck queue");
            foreach(var usedSlot in usedSlots)
            {
                usedSlot.Selected=true;usedSlot.StrikeAllowed=true;usedSlot.SetRuntimeActive(true,true);Check(!usedSlot.StartedFor(session),"new accepted call clears usage "+usedSlot.Type);
            }
            rider.enabled=motorEnabled;
            Debug.Log("REVIEW_SINGLE_USE_PASS duck natural finish/leave/return/same-call re-enable/new accepted call; chicken/tire/melon distance/time; pigeon exit");
        }
        private IEnumerator SnatchOnce()
        {
            var slot=Slot("theft");slot.Selected=true;slot.StrikeAllowed=true;slot.SetRuntimeActive(true,true);
            var bird=slot.GetComponent<PigeonSnatchHazard>();var perch=(Transform)Get(bird,"perch");body.position=perch.position;session.Player.position=body.position;
            Call(bird,"FixedUpdate");Check(Get(bird,"phase").ToString()=="Warning"&&slot.StartedFor(session),"snatch warning consumes the call activation");
            Set(bird,"phase",Enum.Parse(bird.GetType().GetField("phase",Any).FieldType,"Spent"));
            body.position=perch.position+Vector3.forward*100;session.Player.position=body.position;Call(bird,"FixedUpdate");
            Check(Get(bird,"phase").ToString()=="Spent","spent snatch bird cannot rearm after leaving range");yield break;
        }
        private IEnumerator MuscleOnce()
        {
            var slot=Slot("muscleman");var muscle=slot.GetComponent<MusclemanHazard>();
            var previousPath=Get(muscle,"htmlPath");var previousNetwork=Get(muscle,"sidewalks");var previousWind=Get(muscle,"windSeconds");
            var host=new GameObject("EmptyLegacyMuscleRoute");host.SetActive(false);var empty=host.AddComponent<PedestrianNetwork>();
            try
            {
                Set(muscle,"htmlPath",null);Set(muscle,"sidewalks",empty);Set(muscle,"windSeconds",new Vector2(.1f,.1f));
                slot.Selected=true;slot.StrikeAllowed=true;slot.SetRuntimeActive(true,true);body.position=slot.transform.position;session.Player.position=body.position;
                Call(muscle,"FixedUpdate");Check(Get(muscle,"phase").ToString()=="Winding"&&slot.StartedFor(session),"muscle warning consumes call activation");
                Set(muscle,"windElapsed",1f);Call(muscle,"FixedUpdate");Check(Get(muscle,"phase").ToString()=="Spent","failed legacy route cannot restart an already consumed warning");
            }
            finally{Set(muscle,"htmlPath",previousPath);Set(muscle,"sidewalks",previousNetwork);Set(muscle,"windSeconds",previousWind);Destroy(host);}
            yield break;
        }
        private HazardSlot Slot(string type)=>planner.Slots.First(s=>s.Type==type);
        private void Save()=>File.WriteAllText(MeasureRunner.Arg("-reviewOut","Library/html_review_runtime.json"),JsonConvert.SerializeObject(new{failures,measurements},Formatting.Indented));
        private static object Get(object o,string n)=>o.GetType().GetField(n,Any).GetValue(o);
        private static void Set(object o,string n,object v)=>o.GetType().GetField(n,Any).SetValue(o,v);
        private static void Property(object o,string n,object v)=>o.GetType().GetProperty(n,Any).SetValue(o,v);
        private static object Call(object o,string n,params object[] args)=>o.GetType().GetMethod(n,Any).Invoke(o,args);
        private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    }
}
