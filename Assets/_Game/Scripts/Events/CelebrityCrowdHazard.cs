using System.Collections.Generic;
using UnityEngine;

namespace DeliveryRider
{
    // One gathering per placed location each day. Fans remain and keep cheering after they arrive.
    public sealed class CelebrityCrowdHazard : MonoBehaviour, IPlacedHazard
    {
        public const float FanDistance = 2f;
        [SerializeField] private Material celebrityCoat;
        [SerializeField] private Material glowMaterial;
        [SerializeField, Min(0.1f)] private float triggerRadius = 10f;
        [SerializeField, Min(0.1f)] private float spawnRadius = 15f;
        [SerializeField, Min(0)] private int waitingFans = 6;
        [SerializeField, Min(1)] private int incomingFans = 26;
        [SerializeField, Min(0.1f)] private float fanSpeedMultiplier = 3.75f;
        [SerializeField, Min(1f)] private float fanResistance = 2f;
        [SerializeField, Min(0.1f)] private float fanMassMultiplier = 3f;
        [Header("팬 접촉 (즉시 적용)")]
        [SerializeField, Min(0f)] private float fanKnockMinimumSpeed = 8.5f;
        [SerializeField, Range(0f, 1f)] private float fanContactSpeedShare = 0.74f;
        [SerializeField, Min(0.1f)] private float riderMinimumProgressSpeed = 1.5f;
        [SerializeField, Min(0.05f)] private float fanContactCooldown = 0.35f;
        public float FanKnockMinimumSpeed => fanKnockMinimumSpeed;
        public float FanContactSpeedShare => fanContactSpeedShare;
        public float RiderMinimumProgressSpeed => riderMinimumProgressSpeed;
        public float FanContactCooldown => fanContactCooldown;
        private DeliverySession session;
        private PedestrianNetwork network;
        private RoadNetwork ground;
        private HazardSlot slot;
        private float TriggerScale => slot != null ? slot.TriggerScale : 1f;
        private int centerPath;
        private bool centerPrepared;
        public void PrepareCenter(int path,float along){centerPath=path;centerAlong=along;centerPrepared=path>=0;}
        [SerializeField, Min(.1f)] private float crowdSeconds=14f;
        private float crowdEndsAt;
        private float centerAlong, retryAt;
        private Transform celebrity;
        private TrafficPedestrian waitingStar;
        private bool appeared, waitingPrepared;
        private int actorsUsed;
        private const float PrepareRadius=25f;
        private readonly List<TrafficPedestrian> actors = new List<TrafficPedestrian>();
        private readonly List<Vector3> spawnPoints = new List<Vector3>();

        public void Place(DeliverySession owner, RoadNetwork roads, HazardCandidate spot)
        {
            session = owner;
            ground = roads;
        }

        private void Start()=>EnsurePlacement();
        private bool EnsurePlacement()
        {
            if(network==null)network=FindFirstObjectByType<PedestrianNetwork>();
            if(slot==null)slot=GetComponent<HazardSlot>();
            if(session==null||network==null||network.Prefab==null||ground==null)return false;
            if(!centerPrepared)centerPrepared=FindCenter(network,transform.position,out centerPath,out centerAlong);
            return centerPrepared;
        }
        public void ShowWaitingPreview()
        {
            if(!EnsurePlacement())return;
            ClearActors();
            var star=SpawnActor(centerPath,centerAlong,transform.position,false,celebrityCoat,1f,1f,100);
            star.name="Celebrity_Preview";celebrity=star.transform;waitingStar=star;
            if(slot!=null)slot.RefreshOwnedComponents();
        }
        public void EndHeldVisuals()=>ClearActors();

        // Six equally spaced directions, each two metres from the celebrity spawn point.
        public static Vector3 WaitingSpot(Vector3 center, Quaternion rotation, int index)
        {
            float angle = index * Mathf.PI / 3f;
            return center + rotation * new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * FanDistance;
        }

        private void Update()
        {
            if(network!=null&&ground!=null&&(slot==null||slot.Selected)&&!appeared&&!waitingPrepared&&!session.IsSettled&&!session.IsPaused
                &&Vector3.ProjectOnPlane(session.Player.position-transform.position,Vector3.up).sqrMagnitude<=PrepareRadius*PrepareRadius)
            {
                for(int i=0;i<waitingFans;i++)SpawnFan(centerPath,centerAlong,WaitingSpot(transform.position,transform.rotation,i),true,i);
                waitingPrepared=true;if(slot!=null)slot.RefreshOwnedComponents();
            }
            if(appeared)
            {
                if(session.IsSettled||slot!=null&&!slot.StrikeAllowed||Time.time>=crowdEndsAt)ClearActors();
                return;
            }
            if (network == null || !enabled || Time.time < retryAt || session.IsPaused || session.IsSettled || session.IsMapOpen
                || slot != null && !slot.StrikeAllowed) return;
            var offset = session.Player.position - transform.position;
            float reach = triggerRadius * TriggerScale;
            if (Mathf.Abs(offset.y) > 4f * TriggerScale || Vector3.ProjectOnPlane(offset, Vector3.up).sqrMagnitude > reach * reach) return;
            // Resolve all 26 safe positions before spawning anything; a busy area retries instead of producing a partial wave.
            Physics.SyncTransforms();
            if (!FindSpawnPoints()) { retryAt = Time.time + 1f; return; }
            slot?.MarkStarted(session);
            appeared = true; crowdEndsAt=Time.time+crowdSeconds;
            var star = waitingStar!=null&&waitingStar.gameObject.activeSelf?waitingStar:SpawnActor(centerPath, centerAlong, transform.position, false, celebrityCoat, 1f, 1f, 100);
            waitingStar=null;
            star.name = "Celebrity";
            celebrity = star.transform;
            foreach (var actor in actors) if (actor != star&&actor.gameObject.activeSelf) actor.SetCrowdCenter(celebrity);
            for (int i = 0; i < spawnPoints.Count; i++)
            {
                var point = spawnPoints[i];
                SpawnFan(centerPath, centerAlong, point, false, waitingFans + i);
            }
            if(slot!=null)slot.RefreshOwnedComponents();
            Physics.SyncTransforms();
        }

        private TrafficPedestrian SpawnFan(int route, float along, Vector3 spot, bool waiting, int index)
        {
            var actor = SpawnActor(route, along, ground.OnGround(spot, 0f), true, null, fanSpeedMultiplier, fanResistance, index);
            actor.SetCrowdCenter(celebrity != null ? celebrity : transform);
            actor.name = waiting ? $"WaitingFan_{index:00}" : $"IncomingFan_{index:00}";
            var visual = actor.transform.Find("Visual");
            if(actor.transform.Find("Visual/GlowStick")!=null||actor.transform.Find("GlowStick")!=null)return actor;
            var stick = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            stick.name = "GlowStick";
            var collider = stick.GetComponent<Collider>();
            collider.enabled = false;
            Destroy(collider);
            stick.transform.SetParent(visual != null ? visual : actor.transform, false);
            stick.transform.localPosition = new Vector3(0.42f, 1.6f, 0.1f);
            stick.transform.localRotation = Quaternion.Euler(0f, 0f, -22f);
            stick.transform.localScale = new Vector3(0.065f, 0.3f, 0.065f);
            stick.GetComponent<Renderer>().sharedMaterial = glowMaterial;
            var hand = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            hand.name = "GlowStickHand";
            var handCollider = hand.GetComponent<Collider>();
            handCollider.enabled = false;
            Destroy(handCollider);
            hand.transform.SetParent(stick.transform.parent, false);
            hand.transform.localPosition = new Vector3(0.38f, 1.38f, 0.1f);
            hand.transform.localScale = Vector3.one * 0.13f;
            var head = actor.transform.Find("Visual/Head");
            if (head != null) hand.GetComponent<Renderer>().sharedMaterial = head.GetComponent<Renderer>().sharedMaterial;
            return actor;
        }

        private TrafficPedestrian SpawnActor(int route, float along, Vector3 spot, bool fan, Material coat, float speed, float resistance, int seed)
        {
            TrafficPedestrian actor;
            if(actorsUsed<actors.Count)actor=actors[actorsUsed];
            else{actor=Instantiate(network.Prefab,spot,transform.rotation,transform);actors.Add(actor);}
            actorsUsed++;
            actor.transform.SetPositionAndRotation(spot,transform.rotation);
            actor.enabled=true;
            actor.InitializeCrowd(network, route, along, Random.Range(1, 100000) + seed, coat,
                transform, spot, fan, speed, resistance, fan ? fanMassMultiplier : 1f);
            actor.ConfigureCrowdContact(fan?this:null);
            actor.gameObject.SetActive(true);
            return actor;
        }

        private bool FindSpawnPoints()
        {
            spawnPoints.Clear();
            for (int attempt = 0; attempt < 12000 && spawnPoints.Count < incomingFans; attempt++)
            {
                float angle = Random.value * Mathf.PI * 2f;
                float radius = Mathf.Sqrt(Random.Range(16f, Mathf.Max(16f, spawnRadius * spawnRadius)));
                var feet = ground.OnGround(transform.position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius, 0f);
                if (Mathf.Abs(feet.y - transform.position.y) > 0.5f
                    || (feet - session.Player.position).sqrMagnitude < 4f || !Clear(feet)) continue;
                bool spaced = true;
                foreach (var existing in spawnPoints) if ((existing - feet).sqrMagnitude < 0.85f * 0.85f) { spaced = false; break; }
                if (!spaced || !HasDirectApproach(feet, transform.position)) continue;
                spawnPoints.Add(feet);
            }
            return spawnPoints.Count == incomingFans;
        }

        public static bool Clear(Vector3 feet) => !Physics.CheckCapsule(feet + Vector3.up * 0.55f,
            feet + Vector3.up * 1.45f, 0.3f, HazardPhysics.Everything, QueryTriggerInteraction.Ignore);

        // Spawn only where a fan can reach the two-metre ring directly. Do not place fans behind buildings.
        public static bool HasDirectApproach(Vector3 feet, Vector3 center)
        {
            var outward = Vector3.ProjectOnPlane(feet - center, Vector3.up).normalized;
            var target = center + outward * FanDistance;
            var delta = target - feet;
            if (delta.sqrMagnitude < 0.0001f) return true;
            foreach (var hit in Physics.CapsuleCastAll(feet + Vector3.up * 0.65f, feet + Vector3.up * 1.45f,
                0.3f, delta.normalized, delta.magnitude, HazardPhysics.Everything, QueryTriggerInteraction.Ignore))
                if (hit.rigidbody == null && hit.normal.y < 0.7f) return false;
            return true;
        }

        public static bool FindCenter(PedestrianNetwork sidewalks, Vector3 center, out int route, out float along)
        {
            route = -1; along = 0f;
            float best = 4f;
            for (int p = 0; p < sidewalks.PathCount; p++)
            {
                var lane = sidewalks.Path(p);
                if (lane.connector) continue;
                for (int i = 1; i < lane.points.Length; i++)
                {
                    var a = lane.points[i - 1]; var delta = lane.points[i] - a;
                    float t = Mathf.Clamp01(Vector3.Dot(center - a, delta) / Mathf.Max(0.001f, delta.sqrMagnitude));
                    float squared = (Vector3.Lerp(a, lane.points[i], t) - center).sqrMagnitude;
                    if (squared >= best) continue;
                    best = squared; route = p; along = Mathf.Lerp(lane.cumulative[i - 1], lane.cumulative[i], t);
                }
            }
            return route >= 0;
        }

        private void OnEnable()
        {
            ClearActors();
            appeared=slot!=null&&slot.StartedFor(session);retryAt=0f;waitingPrepared=false;
        }

        private void OnDisable()=>ClearActors();

        private void ClearActors()
        {
            foreach(var actor in actors)if(actor!=null)actor.gameObject.SetActive(false);
            actorsUsed=0;celebrity=null;waitingStar=null;waitingPrepared=false;
        }
    }
}
