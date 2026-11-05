using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;

namespace DeliveryRider.Editor
{
    [InitializeOnLoad]
    public static class RecoveryGuardPlayChecks
    {
        private const string Pending="RecoveryGuard.Pending";
        static RecoveryGuardPlayChecks()=>EditorApplication.playModeStateChanged+=Mode;
        public static void Run(){EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);SessionState.SetBool(Pending,true);EditorApplication.EnterPlaymode();}
        private static void Mode(PlayModeStateChange mode)
        {
            if(!SessionState.GetBool(Pending,false))return;
            if(mode==PlayModeStateChange.EnteredPlayMode)new GameObject("RecoveryGuardProbe").AddComponent<RecoveryGuardProbe>();
            if(mode==PlayModeStateChange.EnteredEditMode){SessionState.SetBool(Pending,false);EditorApplication.Exit(SessionState.GetInt(Pending+".exit",1));}
        }
        internal static void Finish(bool ok){Debug.Log(ok?"RECOVERY_GUARD_ALL_PASS":"RECOVERY_GUARD_FAILED");SessionState.SetInt(Pending+".exit",ok?0:1);EditorApplication.ExitPlaymode();}
    }
    public sealed class RecoveryGuardProbe:MonoBehaviour
    {
        private const BindingFlags Any=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private IEnumerator probe;private DeliverySession session;private RiderMotor motor;private Rigidbody body;private RiderSafeRecovery recovery;
        private readonly List<string> failures=new List<string>();private readonly List<object> observations=new List<object>();
        private void Start()=>probe=Probe();
        private void Update(){try{if(probe!=null&&!probe.MoveNext()){probe=null;Save();RecoveryGuardPlayChecks.Finish(failures.Count==0);}}catch(Exception e){failures.Add(e.ToString());Save();RecoveryGuardPlayChecks.Finish(false);probe=null;}}
        private IEnumerator Probe()
        {
            Build();yield return null;
            var cases=new Dictionary<string,Func<IEnumerator>>{{"ManholePosition",()=>Position("Manhole")},{"HydrantPosition",()=>Position("Hydrant")},{"AllBlockedFallback",BlockedFallback},{"RecoveryChain",Guard},{"OrdinaryChain",OrdinaryChain},{"NormalFoodRemoval",NormalFood},{"FoodSceneUnload",UnloadFood},{"RecoveryReasons",Reasons}};
            foreach(var item in cases)
            {
                Call(motor,"ResetReaction");Set(motor,"lastHazard",null);Set(motor,"lastHazardHitAt",float.NegativeInfinity);Set(motor,"nextTrafficHitAt",0f);
                var test=item.Value();bool failed=false;
                while(true)
                {
                    bool next;object current=null;
                    try{next=test.MoveNext();if(next)current=test.Current;}
                    catch(Exception e){failed=true;failures.Add(item.Key+": "+e);Debug.LogError("RECOVERY_GUARD_FAIL "+item.Key+": "+e);break;}
                    if(!next)break;yield return current;
                }
                if(!failed)Debug.Log("RECOVERY_GUARD_PASS "+item.Key);
            }
        }
        private void Build()
        {
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.name="RecoveryFloor";floor.transform.position=new Vector3(0,-.5f,0);floor.transform.localScale=new Vector3(30,1,40);
            var network=new GameObject("RecoveryRoads").AddComponent<RoadNetwork>();network.Configure(new[]{floor.GetComponent<Collider>()});
            var lane=new GameObject("SameRoad");lane.transform.SetParent(network.transform);var spline=lane.AddComponent<SplineContainer>();
            spline.Spline.Add(new BezierKnot(new float3(0,0,-12)));spline.Spline.Add(new BezierKnot(new float3(0,0,12)));lane.AddComponent<RoadSurface>().Configure(RoadKind.Main,7,2);
            var services=new GameObject("InactiveRecoveryServices");services.SetActive(false);session=services.AddComponent<DeliverySession>();
            var player=new GameObject("RecoveryRider");player.SetActive(false);player.transform.position=new Vector3(0,.04f,0);body=player.AddComponent<Rigidbody>();body.constraints=RigidbodyConstraints.FreezeAll;
            var shape=player.AddComponent<CapsuleCollider>();shape.center=Vector3.up*.9f;shape.height=1.8f;shape.radius=.5f;
            recovery=player.AddComponent<RiderSafeRecovery>();Set(recovery,"network",network);motor=player.AddComponent<RiderMotor>();Set(motor,"session",session);Set(motor,"view",new GameObject("RecoveryView").transform);
            Set(session,"player",player.transform);Set(session,"rider",motor);Set(session,"foodBag",new GameObject("RecoveryBag"));Set(session,"openZones",new List<string>{"C"});Property(session,"Progress",new RunProgress(4,100000));
            var pickup=new GameObject("RecoveryPickup").transform;pickup.position=Vector3.right*10;var target=new GameObject("InactiveRecoveryRecipient");target.SetActive(false);var resident=target.AddComponent<DeliveryResident>();
            Property(session,"ActiveOrder",new DeliveryOrderOffer(1,pickup,resident,target.transform,"test","test","test",3000,1800,300,30,"test","C","C",0,0));Property(session,"Stage",DeliveryStage.Carrying);
            player.SetActive(true);motor.enabled=false;Physics.SyncTransforms();
        }
        private GameObject Hazard(string name,Vector3 at)
        {
            var staging=new GameObject("InactiveHazardStaging");staging.SetActive(false);
            var root=Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Game/Prefabs/Hazards/"+name+".prefab"),staging.transform);
            root.transform.position=new Vector3(at.x,0,at.z);root.GetComponent<GroundContactHazard>().Place(session,null,default);root.transform.SetParent(null,true);DestroyImmediate(staging);Physics.SyncTransforms();return root;
        }
        private IEnumerator Position(string kind)
        {
            var from=new Vector3(0,4,0);var initial=recovery.FindSafePosition(from,Vector3.right*10);var hazard=Hazard(kind,initial);
            try
            {
                var actual=recovery.FindSafePosition(from,Vector3.right*10);float distance=Vector2.Distance(new Vector2(actual.x,actual.z),new Vector2(initial.x,initial.z));
                observations.Add(new{check=kind,initial=initial.ToString("F3"),actual=actual.ToString("F3"),distance});
                Check(distance>2.6f&&Mathf.Abs(actual.x)<.01f&&distance<=4.1f,"recovery must choose the nearby clear sample on the same road instead of the burst range");
                Check(recovery.IsSafe(actual),"original standability remains valid");
            }
            finally{DestroyImmediate(hazard);Physics.SyncTransforms();}
            yield break;
        }
        private IEnumerator BlockedFallback()
        {
            var from=new Vector3(0,4,0);var original=recovery.FindSafePosition(from,Vector3.right*10);var hazard=Hazard("Hydrant",original);hazard.GetComponent<BoxCollider>().size=new Vector3(40,2,40);Physics.SyncTransforms();
            try{Check(Vector3.Distance(original,recovery.FindSafePosition(from,Vector3.right*10))<.001f,"all blocked keeps the original recovery fallback");}
            finally{DestroyImmediate(hazard);Physics.SyncTransforms();}yield break;
        }
        private IEnumerator Guard()
        {
            var previous=new GameObject("PreviousHazard");var other=new GameObject("NextHazard");
            try
            {
                Set(motor,"lastHazard",previous);Set(motor,"lastHazardHitAt",Time.time-.5f);Call(motor,"RestoreSafePosition");
                Check(!motor.TryHazardCrash(other,Vector3.up*2,.6f),"another hazard must not bypass the recovery guard through chain");
                Check(!motor.TryHazardTumble(other,.6f,true),"repeat-contact tumble also respects safe recovery guard");
                float until=Time.time+1.05f;while(Time.time<until)yield return null;
                Check(motor.TryHazardCrash(other,Vector3.up*2,.6f),"hazards resume after one second");
            }
            finally{Destroy(previous);Destroy(other);}
        }
        private IEnumerator OrdinaryChain()
        {
            var previous=new GameObject("OrdinaryPrevious");var other=new GameObject("OrdinaryNext");
            Set(motor,"lastHazard",previous);Set(motor,"lastHazardHitAt",Time.time-.5f);Set(motor,"nextTrafficHitAt",Time.time+4);Set(motor,"trafficRecoveryActive",true);
            try{Check(motor.TryHazardCrash(other,Vector3.up*2,.6f),"ordinary accident chains still work during traffic recovery");Check(motor.TryHazardTumble(other,.6f,true),"ordinary repeat-contact tumble remains available");}
            finally{Destroy(previous);Destroy(other);}yield break;
        }
        private DroppedFood Food(DeliverySession owner)
        {
            Property(owner,"Stage",DeliveryStage.Carrying);var host=new GameObject("GuardDroppedFood");var food=host.AddComponent<DroppedFood>();food.Launch(owner,motor.transform,Vector3.up,Vector3.right*8,2,1,null);Call(food,"Update");food.enabled=false;Check(owner.IsChasingFood,"food loss setup");return food;
        }
        private IEnumerator NormalFood()
        {
            var food=Food(session);int notices=0;Action changed=()=>notices++;session.Changed+=changed;
            try{DestroyImmediate(food.gameObject);Check(session.IsRecoveringFood&&!session.IsChasingFood&&notices==1,"ordinary food removal still requests replacement once");}
            finally{session.Changed-=changed;}yield break;
        }
        private IEnumerator UnloadFood()
        {
            var unloading=SceneManager.CreateScene("DroppedFoodUnloadProbe");var host=new GameObject("InactiveUnloadingServices");host.SetActive(false);var owner=host.AddComponent<DeliverySession>();
            Set(owner,"player",motor.transform);Set(owner,"rider",motor);Property(owner,"Progress",new RunProgress(4,100000));Property(owner,"ActiveOrder",session.ActiveOrder);
            var bag=new GameObject("UnloadingFoodBag");SceneManager.MoveGameObjectToScene(bag,unloading);Set(owner,"foodBag",bag);var food=Food(owner);SceneManager.MoveGameObjectToScene(food.gameObject,unloading);
            int errors=0;Application.LogCallback log=(message,stack,type)=>{if(type==LogType.Exception&&message.Contains("MissingReferenceException")&&stack.Contains("DeliverySession"))errors++;};Application.logMessageReceived+=log;
            try{DestroyImmediate(bag);var operation=SceneManager.UnloadSceneAsync(unloading);while(!operation.isDone)yield return null;Check(errors==0,"unloading after bag destruction must not notify the destroyed UI/food bag");}
            finally{Application.logMessageReceived-=log;Destroy(host);}
        }
        private IEnumerator Reasons()
        {
            var messages=new List<string>();Application.LogCallback log=(message,stack,type)=>{if(message.StartsWith("RIDER_RECOVERY "))messages.Add(message);};Application.logMessageReceived+=log;
            GameObject roof=null;
            try
            {
                foreach(bool timeout in new[]{true,false})
                {
                    Call(motor,"ResetReaction");body.position=new Vector3(0,12,0);body.linearVelocity=Vector3.zero;Physics.SyncTransforms();
                    motor.GetType().GetMethod("SetReactionState",Any).Invoke(motor,new object[]{RiderReactionState.Airborne});
                    Set(motor,"reactionStartedAt",Time.time-(timeout?9f:.5f));Set(motor,"noProgressTime",timeout?0f:3f);Set(motor,"lastProgressPosition",body.position);
                    Call(motor,"TickReaction");Check(messages.Last().Contains("reason="+(timeout?"AccidentTimeout":"Stuck")),"recovery reason follows the unchanged timeout/stuck branch");
                }
                roof=GameObject.CreatePrimitive(PrimitiveType.Cube);roof.name="WalkableRoof";roof.transform.position=new Vector3(0,3,0);roof.transform.localScale=new Vector3(3,.4f,3);
                Call(motor,"ResetReaction");body.position=new Vector3(0,3.24f,0);body.linearVelocity=Vector3.zero;Physics.SyncTransforms();
                motor.GetType().GetMethod("SetReactionState",Any).Invoke(motor,new object[]{RiderReactionState.Airborne});Set(motor,"reactionStartedAt",Time.time-9);Set(motor,"lastProgressPosition",body.position);
                int count=messages.Count;Call(motor,"TickReaction");Check(motor.IsReactionGrounded&&messages.Count==count&&body.position.y>3,"walkable roof does not itself trigger recovery");
                observations.Add(new{check="RecoveryReasons",timeout=1,stuck=1,walkableRoofRecoveries=0});
            }
            finally{Application.logMessageReceived-=log;if(roof!=null)DestroyImmediate(roof);}
            yield break;
        }
        private void Save()=>File.WriteAllText(MeasureRunner.Arg("-recoveryOut",@"C:\UnityWork\ge\공유\결과\codex_recovery_play.json"),JsonConvert.SerializeObject(new{failures,observations},Formatting.Indented));
        private static object Get(object o,string n)=>o.GetType().GetField(n,Any).GetValue(o);
        private static void Set(object o,string n,object v)=>o.GetType().GetField(n,Any).SetValue(o,v);
        private static void Property(object o,string n,object v)=>o.GetType().GetProperty(n,Any).SetValue(o,v);
        private static object Call(object o,string n)=>o.GetType().GetMethod(n,Any).Invoke(o,null);
        private static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    }
}
