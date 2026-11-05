using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeliveryRider.Editor
{
    // Flat-floor measurement of the production motor with the real scene's serialized movement settings.
    [InitializeOnLoad]
    public static class HtmlFlightPlayChecks
    {
        private const string Pending="HtmlFlight.Pending";
        static HtmlFlightPlayChecks()=>EditorApplication.playModeStateChanged+=Mode;
        public static void Run()
        {
            var scene=EditorSceneManager.OpenScene("Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity",OpenSceneMode.Single);
            var source=scene.GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren<RiderMotor>(true)).Single();
            SessionState.SetString(Pending+".motor",EditorJsonUtility.ToJson(source));
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            SessionState.SetBool(Pending,true);SessionState.SetInt(Pending+".exit",1);EditorApplication.EnterPlaymode();
        }
        private static void Mode(PlayModeStateChange state)
        {
            if(!SessionState.GetBool(Pending,false))return;
            if(state==PlayModeStateChange.EnteredPlayMode)new GameObject("Flat flight measurement").AddComponent<HtmlFlightMeasurement>();
            if(state==PlayModeStateChange.EnteredEditMode){SessionState.SetBool(Pending,false);EditorApplication.Exit(SessionState.GetInt(Pending+".exit",1));}
        }
        internal static string MotorJson=>SessionState.GetString(Pending+".motor","");
        internal static void Finish(Exception error)
        {
            if(error==null)Debug.Log("HTML_FLAT_FLIGHT_PASS: real motor, real C values, measured flat-floor height/distance/bounces");else Debug.LogError("HTML_FLAT_FLIGHT_FAIL "+error);
            SessionState.SetInt(Pending+".exit",error==null?0:1);EditorApplication.ExitPlaymode();
        }
    }
    public sealed class HtmlFlightMeasurement:MonoBehaviour
    {
        private const BindingFlags Any=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private readonly List<object> rows=new List<object>();
        private readonly string[] kinds={"manhole","hydrant","duck","muscleman","tire","melon","cat_jab","cat_hook","cat_upper","cat_loss","cat_chase","dog"};
        private RiderMotor motor;private Rigidbody body;private DeliverySession session;
        private int index;private bool launched;private float started,peak,firstAir=-1f,firstDistance;private Vector3 origin,velocity;
        private void Start()
        {
            Time.timeScale=10f;
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.name="1000m flat test floor";floor.transform.position=Vector3.down*.5f;floor.transform.localScale=new Vector3(1000f,1f,1000f);
            var services=new GameObject("Inactive services");services.SetActive(false);session=services.AddComponent<DeliverySession>();
            var player=new GameObject("Rider");player.SetActive(false);body=player.AddComponent<Rigidbody>();body.mass=75f;body.constraints=RigidbodyConstraints.FreezeRotation;
            var shape=player.AddComponent<CapsuleCollider>();shape.center=Vector3.up*.9f;shape.height=1.8f;shape.radius=.5f;
            motor=player.AddComponent<RiderMotor>();EditorJsonUtility.FromJsonOverwrite(HtmlFlightPlayChecks.MotorJson,motor);
            Set(session,"player",player.transform);Set(session,"rider",motor);Property(session,"Stage",DeliveryStage.Carrying);Property(session,"Progress",new RunProgress(4,100000));
            Set(motor,"session",session);Set(motor,"view",new GameObject("Forward view").transform);Set(motor,"speed",6f);Set(motor,"nextZoneCheckAt",float.PositiveInfinity);
            player.transform.position=Vector3.up*.02f;player.SetActive(true);Next();
        }
        private void Next()
        {
            Call(motor,"ResetReaction");Set(motor,"lastHazardHitAt",float.NegativeInfinity);Set(motor,"lastHazard",null);Set(motor,"nextTrafficHitAt",float.NegativeInfinity);
            origin=new Vector3(0f,.02f,0f);body.position=origin;motor.transform.position=origin;body.linearVelocity=body.angularVelocity=Vector3.zero;Physics.SyncTransforms();
            launched=false;started=Time.time;peak=origin.y;firstAir=-1f;firstDistance=0f;
        }
        private void FixedUpdate()
        {
            try
            {
                if(!launched)
                {
                    if(Time.time-started<.06f)return;
                    velocity=Velocity(kinds[index],out var source);
                    if(!motor.TryHazardCrash(source,velocity,.6f))throw new InvalidOperationException("launch rejected "+kinds[index]);
                    launched=true;started=Time.time;return;
                }
                peak=Mathf.Max(peak,body.position.y);
                int bounces=(int)Get(motor,"floorBounces");
                if(firstAir<0f&&(bounces>0||motor.ReactionState==RiderReactionState.Rolling||motor.ReactionState==RiderReactionState.Normal))
                {firstAir=Time.time-started;firstDistance=Vector3.ProjectOnPlane(body.position-origin,Vector3.up).magnitude;}
                if(Time.time-started<.2f)return;
                if(motor.ReactionState!=RiderReactionState.Normal&&Time.time-started<20f)return;
                if(motor.ReactionState!=RiderReactionState.Normal)throw new InvalidOperationException("flight stuck "+kinds[index]);
                var total=Vector3.ProjectOnPlane(body.position-origin,Vector3.up).magnitude;
                var targets=new System.Collections.Generic.Dictionary<string,Vector2>{{"manhole",new Vector2(13.3f,70f)},{"hydrant",new Vector2(20.8f,29f)},{"duck",new Vector2(5.7f,43f)},{"tire",new Vector2(4f,37f)}};
                if(targets.TryGetValue(kinds[index],out var target)&&((Mathf.Abs(peak-origin.y-target.x)/target.x)>.1f||Mathf.Abs(total-target.y)/target.y>.1f||bounces==0))throw new InvalidOperationException("flat C target outside 10%: "+kinds[index]);
                rows.Add(new{type=kinds[index],horizontal=Vector3.ProjectOnPlane(velocity,Vector3.up).magnitude,up=velocity.y,height=peak-origin.y,total,firstAir,firstDistance,bounces,duration=Time.time-started});
                Debug.Log($"HTML_FLAT type={kinds[index]} h={peak-origin.y:F3} total={total:F3} firstAir={firstAir:F3} bounce={bounces}");
                File.WriteAllText("Library/html_flat_flight.json",JsonConvert.SerializeObject(rows,Formatting.Indented));
                if(++index<kinds.Length){Next();return;}
                HtmlFlightPlayChecks.Finish(null);enabled=false;
            }
            catch(Exception error){enabled=false;HtmlFlightPlayChecks.Finish(error);}
        }
        private Vector3 Velocity(string kind,out UnityEngine.Object source)
        {
            string prefab=kind=="manhole"?"Manhole":kind=="hydrant"?"Hydrant":kind=="duck"?"DuckFlock":kind=="muscleman"?"Muscleman":kind=="tire"?"RollingTire":kind=="melon"?"MelonPile":kind=="dog"?"Dog":"CatBoxer";
            var root=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Game/Prefabs/Hazards/"+prefab+".prefab");
            source=root.GetComponents<MonoBehaviour>().First(p=>p is IPlacedHazard);
            var part=source;
            float Number(string name)=>Convert.ToSingle(part.GetType().GetField(name,Any).GetValue(part));
            Vector2 Pair(string name)=>(Vector2)part.GetType().GetField(name,Any).GetValue(part);
            if(kind=="manhole")return Vector3.forward*(Number("forwardImpulse")+6f*Number("riderSpeedShare"))+Vector3.up*Number("upwardImpulse");
            if(kind=="hydrant")return Vector3.forward*Number("forwardImpulse")+Vector3.up*Number("upwardImpulse");
            if(kind=="duck")return Vector3.forward*Number("bounceImpulse")+Vector3.up*Number("bounceLift");
            if(kind=="muscleman")return Vector3.right*Number("shoveSpeed")+Vector3.forward*(Number("shoveForward")+6f*Number("shoveRiderShare"))+Vector3.up*Number("shoveLift");
            if(kind=="tire")return Vector3.forward*Number("impulse")+Vector3.up*Number("upwardImpulse");
            if(kind=="melon")return Vector3.forward*Number("hitSpeed")+Vector3.up*Number("hitLift");
            if(kind=="dog")return Vector3.forward*Number("bounceSpeed")+Vector3.up*Number("bounceLift");
            if(kind=="cat_jab")return Vector3.forward*Number("jabForward")+Vector3.up*Number("jabUp");
            if(kind=="cat_hook")return Vector3.right*Number("hookSide")+Vector3.forward*Number("hookForward")+Vector3.up*Number("hookUp");
            if(kind=="cat_upper")return Vector3.forward*Number("upperForward")+Vector3.up*((Pair("upperUp").x+Pair("upperUp").y)*.5f);
            if(kind=="cat_loss")return Vector3.forward*Number("lossForward")+Vector3.up*((Pair("lossUp").x+Pair("lossUp").y)*.5f);
            return Vector3.forward*Number("chaseForward")+Vector3.up*Number("chaseUp");
        }
        private static object Get(object owner,string name)=>owner.GetType().GetField(name,Any).GetValue(owner);
        private static void Set(object owner,string name,object value)=>owner.GetType().GetField(name,Any).SetValue(owner,value);
        private static void Property(object owner,string name,object value)=>owner.GetType().GetProperty(name,Any).SetValue(owner,value);
        private static object Call(object owner,string name)=>owner.GetType().GetMethod(name,Any).Invoke(owner,null);
    }
}
