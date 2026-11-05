using TMPro;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

namespace DeliveryRider
{
    // HTML '낙': track, lock the visible target, then resolve one ground stun per call.
    public sealed class FallingObjectHazard : MonoBehaviour, IPlacedHazard
    {
        private enum Phase { Ready, Aiming, Impact, Spent }
        [SerializeField] private DeliverySession session;
        [SerializeField] private Transform crow;
        [SerializeField] private Transform fallingObject;
        [SerializeField] private Transform aimMarker;
        [SerializeField] private TMP_Text cue;
        [SerializeField] private Transform[] fragments = System.Array.Empty<Transform>();
        [Header("낙하물 — HTML 시험값")]
        [SerializeField, Min(.1f)] private float triggerRadius = 9f;
        [SerializeField, Min(0f)] private float initialLeadSeconds = .9f;
        [SerializeField, Min(0f)] private float trackingLeadSeconds = .6f;
        [SerializeField, Min(0f)] private float trackingSeconds = 1.3f;
        [SerializeField, Min(.1f)] private float impactSeconds = 1.9f;
        [SerializeField, Min(.1f)] private float hitRadius = 1.7f;
        [SerializeField, Min(.1f)] private float stunSeconds = 2.6f;
        [Header("낙하물 표시")]
        [SerializeField, Min(.1f)] private float fallHeight = 6f;
        [SerializeField, Min(.1f)] private float effectSeconds = .6f;
        [SerializeField, Min(.1f)] private float fragmentSpread = 2f;
        private RiderMotor rider;
        private HazardSlot slot;
        private RoadNetwork ground;
        private RoadSurface[] roads = System.Array.Empty<RoadSurface>();
        private HazardZoneFilter zones;
        private DeliveryOrderOffer observedOrder;
        private Camera viewCamera;
        private Phase phase;
        private bool used;
        private float elapsed;
        private Vector3 target;
        private float TriggerScale => slot != null ? slot.TriggerScale : 1f;
        private bool CanStrike => slot != null ? slot.StrikeAllowed : HazardPlanner.DeliveryInProgress(session);

        public void Place(DeliverySession owner, RoadNetwork network, HazardCandidate spot)
        {
            session = owner;
            ground = network;
            if (ground != null)
            {
                roads = new RoadSurface[ground.Roads.Count];
                for (int i = 0; i < roads.Length; i++) roads[i] = ground.Roads[i];
            }
            if (crow != null && ground != null) crow.position = ground.OnGround(transform.position, fallHeight);
        }

        private void Awake()
        {
            rider = session != null && session.Player != null ? session.Player.GetComponent<RiderMotor>() : null;
            slot = GetComponent<HazardSlot>();
            if (rider == null || crow == null || fallingObject == null || aimMarker == null) { enabled = false; return; }
            zones = new HazardZoneFilter(session.ZoneMap, session.IsZoneOpen);
            viewCamera = Camera.main;
            observedOrder = session.ActiveOrder;
        }

        private void OnEnable() { phase = used ? Phase.Spent : Phase.Ready; HideEffects(); }
        private void OnDisable()
        {
            // Disabling mid-warning consumes this call, so re-enabling cannot create a late invisible hit.
            if (phase == Phase.Aiming || phase == Phase.Impact) used = true;
            phase = Phase.Spent;
            elapsed = 0f;
            HideEffects();
        }

        private void FixedUpdate()
        {
            if (session == null || rider == null) return;
            if (!ReferenceEquals(observedOrder, session.ActiveOrder))
            {
                observedOrder = session.ActiveOrder;
                used = false;
                phase = Phase.Ready;
                elapsed = 0f;
                HideEffects();
            }
            if (session.IsPaused) return;
            if (session.IsMapOpen || session.IsSettled || !CanStrike)
            {
                if (phase == Phase.Aiming || phase == Phase.Impact) Finish();
                return;
            }
            switch (phase)
            {
                case Phase.Ready:
                    var offset = rider.transform.position - transform.position;
                    float radius = triggerRadius * TriggerScale;
                    if (!used && !rider.IsRecovering && Mathf.Abs(offset.y) < 3f
                        && new Vector2(offset.x, offset.z).sqrMagnitude < radius * radius
                        && OpenSegment(transform.position, rider.transform.position))
                    {
                        used = true;
                        phase = Phase.Aiming;
                        elapsed = 0f;
                        Aim(initialLeadSeconds);
                        ShowCue("까악!");
                        aimMarker.gameObject.SetActive(true);
                        fallingObject.gameObject.SetActive(true);
                        ShowAim();
                    }
                    break;
                case Phase.Aiming:
                    elapsed += Time.fixedDeltaTime;
                    if (!OpenSegment(transform.position, rider.transform.position)) { Finish(); break; }
                    if (elapsed < trackingSeconds) Aim(trackingLeadSeconds);
                    ShowAim();
                    if (elapsed >= impactSeconds) Impact();
                    break;
                case Phase.Impact:
                    elapsed += Time.fixedDeltaTime;
                    for (int i = 0; i < fragments.Length; i++)
                    {
                        if (fragments[i] == null) continue;
                        float angle = i * Mathf.PI * 2f / fragments.Length;
                        float t = Mathf.Clamp01(elapsed / effectSeconds);
                        fragments[i].position = target + new Vector3(Mathf.Cos(angle) * fragmentSpread * t,
                            .1f + Mathf.Sin(t * Mathf.PI) * .8f, Mathf.Sin(angle) * fragmentSpread * t);
                    }
                    if (elapsed >= effectSeconds) Finish();
                    break;
            }
        }

        // Sample the entire line, not just its endpoints: neither detection nor prediction crosses a closed zone.
        private bool OpenSegment(Vector3 from, Vector3 to)
        {
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(from, to) / .25f));
            for (int i = 0; i <= steps; i++) if (!zones.PointOpen(Vector3.Lerp(from, to, i / (float)steps))) return false;
            return true;
        }

        private void Aim(float lead)
        {
            Vector3 from = rider.transform.position;
            Vector3 predicted = SnapToRoad(from + Vector3.ProjectOnPlane(rider.Velocity, Vector3.up) * lead);
            // Stop before the first closed sample, retaining the existing 1 m wall clearance.
            target = from;
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(from, predicted) / .25f));
            for (int i = 1; i <= steps; i++)
            {
                var point = Vector3.Lerp(from, predicted, i / (float)steps);
                if (!zones.PointOpen(point)) break;
                target = point;
            }
            target = ground != null ? ground.OnGround(target, 0f) : new Vector3(target.x, transform.position.y, target.z);
        }

        private Vector3 SnapToRoad(Vector3 point)
        {
            float closest = float.PositiveInfinity;
            Vector3 result = point;
            foreach (var road in roads)
            {
                if (road == null) continue;
                var spline = road.Container;
                SplineUtility.GetNearestPoint(spline.Spline, (float3)spline.transform.InverseTransformPoint(point), out float3 nearest, out _, 4, 2);
                var center = spline.transform.TransformPoint(nearest);
                var offset = Vector3.ProjectOnPlane(point - center, Vector3.up);
                float distance = offset.magnitude - road.HalfWidth;
                if (distance >= closest) continue;
                closest = distance;
                result = distance <= -.45f ? point : center + Vector3.ClampMagnitude(offset, Mathf.Max(0f, road.HalfWidth - .7f));
            }
            return result;
        }

        private void ShowAim()
        {
            aimMarker.position = target + Vector3.up * .025f;
            aimMarker.localScale = new Vector3(hitRadius * TriggerScale * 2f, .02f, hitRadius * TriggerScale * 2f);
            float fall = Mathf.InverseLerp(trackingSeconds, Mathf.Max(trackingSeconds + .01f, impactSeconds), elapsed);
            fallingObject.position = target + Vector3.up * (.15f + fallHeight * (1f - fall * fall));
        }

        private void Impact()
        {
            aimMarker.gameObject.SetActive(false);
            fallingObject.gameObject.SetActive(false);
            phase = Phase.Impact;
            elapsed = 0f;
            foreach (var fragment in fragments) if (fragment != null) fragment.gameObject.SetActive(true);
            ShowCue("쾅!");
            var offset = rider.transform.position - target;
            float radius = hitRadius * TriggerScale;
            if (CanStrike && Mathf.Abs(offset.y) < 3f && new Vector2(offset.x, offset.z).sqrMagnitude < radius * radius
                && zones.PointOpen(target) && OpenSegment(target, rider.transform.position)) rider.TryHazardStun(this, stunSeconds);
        }

        private void ShowCue(string text)
        {
            if (cue == null) return;
            cue.text = text;
            cue.transform.position = target + Vector3.up * 2f;
            cue.gameObject.SetActive(true);
        }

        private void LateUpdate()
        {
            if (cue != null && cue.gameObject.activeSelf && viewCamera != null) cue.transform.rotation = viewCamera.transform.rotation;
        }

        private void Finish() { phase = Phase.Spent; elapsed = 0f; HideEffects(); }
        private void HideEffects()
        {
            if (aimMarker != null) aimMarker.gameObject.SetActive(false);
            if (fallingObject != null) fallingObject.gameObject.SetActive(false);
            if (cue != null) cue.gameObject.SetActive(false);
            foreach (var fragment in fragments) if (fragment != null) fragment.gameObject.SetActive(false);
        }
    }
}
