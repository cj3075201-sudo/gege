using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeliveryRider.Editor
{
    public static class BelowGroundChecks
    {
        public static void Audit()
        {
            EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);Physics.SyncTransforms();
            var network=UnityEngine.Object.FindFirstObjectByType<RoadNetwork>();var motor=UnityEngine.Object.FindFirstObjectByType<RiderMotor>();
            var ground=(Collider[])typeof(RoadNetwork).GetField("ground",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(network);
            var sites=new[]{new Vector3(192,-5.31f,102.86f),new Vector3(201.6f,-5.2f,49),new Vector3(-4.3f,-5.26f,-161.5f),new Vector3(-7.32f,-5.26f,-161.97f),new Vector3(-25.5f,-5.26f,-162.5f),new Vector3(-104,-5.26f,-98.7f),new Vector3(-104.9f,-5.38f,-29.7f),new Vector3(-104.5f,-5.26f,-61.5f),new Vector3(230.1f,-5.62f,.19f),new Vector3(-50.26f,-5.62f,-212.38f)};
            var rows=sites.Select(p=>new{point=V(p),ground=ground.Select(c=>new{collider=PathOf(c.transform),mesh=(c as MeshCollider)?.sharedMesh?.name,boundsMin=V(c.bounds.min),boundsMax=V(c.bounds.max),insideXZ=Inside(c.bounds,p),hit=c.Raycast(new Ray(new Vector3(p.x,100,p.z),Vector3.down),out _,200)}).ToArray(),hits=Physics.RaycastAll(new Vector3(p.x,100,p.z),Vector3.down,200,~0,QueryTriggerInteraction.Ignore).OrderBy(h=>h.distance).Select(h=>new{collider=PathOf(h.collider.transform),layer=h.collider.gameObject.layer,point=V(h.point),normal=V(h.normal)}).ToArray(),nearestRoad=network.Roads.OrderBy(r=>r.DistanceToCentre(p)).Take(1).Select(r=>new{name=r.name,distance=r.DistanceToCentre(p),halfWidth=r.HalfWidth}).FirstOrDefault()}).ToArray();
            var floorMeshes=ground.Select(c=>new{collider=PathOf(c.transform),mesh=(c as MeshCollider)?.sharedMesh?.name,renderMesh=c.GetComponent<MeshFilter>()?.sharedMesh?.name,boundsMin=V(c.bounds.min),boundsMax=V(c.bounds.max),layer=c.gameObject.layer,enabled=c.enabled,trigger=c.isTrigger}).ToArray();
            var body=motor.GetComponent<Rigidbody>();var shape=motor.GetComponent<CapsuleCollider>();
            var result=new{ground=floorMeshes,sites=rows,player=new{collision=body.collisionDetectionMode.ToString(),body.detectCollisions,body.isKinematic,shape.enabled,layer=motor.gameObject.layer,ignoresGroundLayer=Physics.GetIgnoreLayerCollision(motor.gameObject.layer,0)}};
            File.WriteAllText(MeasureRunner.Arg("-belowAuditOut",@"C:\UnityWork\ge\공유\결과\codex_below_ground_audit_before.json"),JsonConvert.SerializeObject(result,Formatting.Indented));
            foreach(var row in rows)Debug.Log($"BELOW_GROUND_AUDIT point={row.point} groundHits={row.ground.Count(x=>x.hit)} insideXZ={row.ground.Any(x=>x.insideXZ)} anyHits={row.hits.Length}");
        }
        public static void ApplyBoundary()
        {
            var scene=EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);
            int layer=LayerMask.NameToLayer("ZoneBarrier");if(layer<0)throw new InvalidOperationException("Existing player-only barrier layer missing");
            var network=UnityEngine.Object.FindFirstObjectByType<RoadNetwork>();
            var ground=(Collider[])typeof(RoadNetwork).GetField("ground",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(network);
            if(ground.Length!=1)throw new InvalidOperationException("Boundary installer expects the single Myeongji ground mesh");
            var bounds=ground[0].bounds;
            var previous=scene.GetRootGameObjects().FirstOrDefault(g=>g.name=="MapBoundary");if(previous!=null)UnityEngine.Object.DestroyImmediate(previous);
            var root=new GameObject("MapBoundary");
            const float thickness=.6f,height=128f;float y=bounds.min.y-8f+height*.5f;
            void Wall(string name,Vector3 position,Vector3 size)
            {
                var wall=new GameObject(name){layer=layer};wall.transform.SetParent(root.transform,false);wall.transform.position=position;wall.AddComponent<BoxCollider>().size=size;
            }
            Wall("West",new Vector3(bounds.min.x-thickness*.5f,y,bounds.center.z),new Vector3(thickness,height,bounds.size.z+2*thickness));
            Wall("East",new Vector3(bounds.max.x+thickness*.5f,y,bounds.center.z),new Vector3(thickness,height,bounds.size.z+2*thickness));
            Wall("South",new Vector3(bounds.center.x,y,bounds.min.z-thickness*.5f),new Vector3(bounds.size.x+2*thickness,height,thickness));
            Wall("North",new Vector3(bounds.center.x,y,bounds.max.z+thickness*.5f),new Vector3(bounds.size.x+2*thickness,height,thickness));
            var player=UnityEngine.Object.FindFirstObjectByType<RiderMotor>().GetComponent<CapsuleCollider>();player.includeLayers|=1<<layer;
            EditorSceneManager.MarkSceneDirty(scene);if(!EditorSceneManager.SaveScene(scene))throw new IOException("Boundary scene save failed");
            Debug.Log($"MAP_BOUNDARY_APPLIED four static player-only colliders at ground bounds {bounds.min}..{bounds.max}; no renderers or tuning values changed");
        }
        internal static float[] V(Vector3 p)=>new[]{p.x,p.y,p.z};
        internal static bool Inside(Bounds b,Vector3 p)=>p.x>=b.min.x&&p.x<=b.max.x&&p.z>=b.min.z&&p.z<=b.max.z;
        private static string PathOf(Transform t)=>t.parent==null?t.name:PathOf(t.parent)+"/"+t.name;
    }

    [InitializeOnLoad]
    public static class BelowGroundTrace
    {
        private static readonly string Output=MeasureRunner.Arg("-belowGroundTraceOut","");
        static BelowGroundTrace()
        {
            if(Output.Length==0)return;
            EditorApplication.playModeStateChanged+=mode=>{if(mode==PlayModeStateChange.EnteredPlayMode)Attach();};
            SceneManager.sceneLoaded+=(scene,mode)=>{if(EditorApplication.isPlaying)Attach();};
            Application.logMessageReceived+=(message,stack,type)=>{if(message.StartsWith("MEASURE run drive"))Write(new{kind="run",message});};
        }
        private static void Attach()
        {
            var motor=UnityEngine.Object.FindFirstObjectByType<RiderMotor>();
            if(motor!=null&&motor.GetComponent<BelowGroundTraceProbe>()==null)motor.gameObject.AddComponent<BelowGroundTraceProbe>();
        }
        internal static void Write(object value)=>File.AppendAllText(Output,JsonConvert.SerializeObject(value)+"\n");
    }
    [DefaultExecutionOrder(10000)]
    public sealed class BelowGroundTraceProbe:MonoBehaviour
    {
        private struct Frame{public float t;public Vector3 p,v;public string state;public bool support;}
        private readonly Queue<Frame> frames=new Queue<Frame>();private RiderMotor motor;private Rigidbody body;private object lastSource;
        private void Awake(){motor=GetComponent<RiderMotor>();body=GetComponent<Rigidbody>();motor.HazardHit+=Hit;motor.ReactionImpulse+=Impulse;motor.SafePositionRestored+=Restored;Application.logMessageReceived+=Log;}
        private void OnDestroy(){if(motor!=null){motor.HazardHit-=Hit;motor.ReactionImpulse-=Impulse;motor.SafePositionRestored-=Restored;}Application.logMessageReceived-=Log;}
        private void FixedUpdate(){frames.Enqueue(new Frame{t=Time.time,p=body.position,v=body.linearVelocity,state=motor.ReactionState.ToString(),support=motor.IsReactionGrounded});while(frames.Count>300)frames.Dequeue();}
        private void Hit(UnityEngine.Object source){lastSource=new{name=source.name,type=source.GetType().Name,position=source is Component c?BelowGroundChecks.V(c.transform.position):null,slot=source is Component part?part.GetComponent<HazardSlot>()?.Type:null};BelowGroundTrace.Write(new{kind="hit",time=Time.time,position=BelowGroundChecks.V(body.position),source=lastSource});}
        private void Impulse(Vector3 velocity)=>BelowGroundTrace.Write(new{kind="impulse",time=Time.time,position=BelowGroundChecks.V(body.position),velocity=BelowGroundChecks.V(velocity)});
        private void Log(string message,string stack,LogType type)
        {
            if(!message.StartsWith("RIDER_RECOVERY reason=BelowGround"))return;
            BelowGroundTrace.Write(new{kind="belowGround",message,time=Time.time,position=BelowGroundChecks.V(body.position),velocity=BelowGroundChecks.V(body.linearVelocity),source=lastSource,trace=frames.Select(f=>new{f.t,p=BelowGroundChecks.V(f.p),v=BelowGroundChecks.V(f.v),f.state,f.support}).ToArray()});
        }
        private void Restored()=>BelowGroundTrace.Write(new{kind="restore",time=Time.time,position=BelowGroundChecks.V(body.position)});
    }
}
