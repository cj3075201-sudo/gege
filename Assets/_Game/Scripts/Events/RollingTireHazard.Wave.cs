using UnityEngine;

namespace DeliveryRider
{
    public sealed partial class RollingTireHazard
    {
        [Header("HTML 타이어 한 개 / 작은 떼")]
        [SerializeField, Range(0f,1f)] private float largeChance = .5f;
        [SerializeField, Min(1)] private int smallCount = 5;
        [SerializeField, Min(.1f)] private float smallSpeed = 9f;
        [SerializeField, Min(.1f)] private float largeSpeed = 12f;
        [SerializeField, Min(.1f)] private float largeRadius = 1.5f;
        [SerializeField, Min(.1f)] private float smallImpulse = 7f;
        [SerializeField, Min(.1f)] private float smallUpwardImpulse = 6.75f;
        [SerializeField, Min(.1f)] private float waveSpacing = 2.6f;
        private HazardZoneFilter waveZones;
        private Transform[] waveWheels;
        private bool[] waveHits;
        private Vector3 originalWheelScale;
        private float originalRadius;
        private bool waveLarge = true, wavePrepared;
        private object preparedOrder;
        private float preparedLargeChance;
        public bool KeepsFoodOnCurrentHit => !waveLarge;
        private int currentWheel;
        private float CurrentWheelHeight => Mathf.Max(WheelHeight, radius + .1f);
        private void InitializeWave()
        {
            waveZones = new HazardZoneFilter(session.ZoneMap,session.IsZoneOpen);
            originalRadius = radius; originalWheelScale = wheel.localScale;
            waveWheels = new Transform[Mathf.Max(1, smallCount)]; waveHits = new bool[waveWheels.Length];
            waveWheels[0] = wheel;
            for(int i=1;i<waveWheels.Length;i++)
            {
                waveWheels[i] = Instantiate(wheel, wheel.parent); waveWheels[i].name = "PooledSmallTire_"+i;
                waveWheels[i].gameObject.SetActive(false);
            }
        }
        private void PrepareWave()
        {
            if(!wavePrepared||!ReferenceEquals(preparedOrder,session.ActiveOrder)||!Mathf.Approximately(preparedLargeChance,largeChance))
            {
                waveLarge=Random.value<largeChance;preparedOrder=session.ActiveOrder;preparedLargeChance=largeChance;wavePrepared=true;
            }
            radius = waveLarge ? Mathf.Max(originalRadius,largeRadius) : originalRadius;
            wheel.localScale = originalWheelScale * (radius/Mathf.Max(.01f,originalRadius));
            for(int i=0;i<waveHits.Length;i++) waveHits[i]=false;
            HideWave(); currentWheel=0;
        }
        private bool CheckWaveContact(Vector3 from,Vector3 direction,float travel)
        {
            bool blocked=false;
            int count=waveLarge?1:waveWheels.Length;
            for(int i=0;i<count;i++)
            {
                currentWheel=i;hitThisCycle=waveHits[i];
                var origin=from-direction*(i*waveSpacing);
                if(road!=null)origin=road.OnGround(origin,CurrentWheelHeight);
                if(i>0&&!OpenWaveStep(from,origin))continue;
                bool obstacle=CheckContact(origin,direction,travel);
                waveHits[i]=hitThisCycle;
                if(i==0)blocked=obstacle;
            }
            currentWheel=0;hitThisCycle=waveHits[0];return blocked;
        }
        private void ShowWave(Vector3 direction)
        {
            if(waveWheels==null)return;
            for(int i=1;i<waveWheels.Length;i++)
            {
                var part=waveWheels[i];
                if(waveLarge){part.gameObject.SetActive(false);continue;}
                var point=wheel.position-direction*(i*waveSpacing);
                if(road!=null)point=road.OnGround(point,CurrentWheelHeight);
                // A trailing wheel never appears through a closed wall or a solid obstacle.
                bool clear=OpenWaveStep(wheel.position,point);
                part.gameObject.SetActive(clear);
                if(clear)part.SetPositionAndRotation(point,wheel.rotation);
            }
        }
        private bool OpenWaveStep(Vector3 from,Vector3 to)
        {
            if(waveZones!=null&&!waveZones.PointOpen(to))return false;
            return !Physics.Linecast(from,to,collisionMask,QueryTriggerInteraction.Ignore);
        }
        private void HideWave()
        {
            if(waveWheels==null)return;
            for(int i=1;i<waveWheels.Length;i++)waveWheels[i].gameObject.SetActive(false);
        }
    }
}
