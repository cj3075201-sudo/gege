using System;
using UnityEngine;

namespace DeliveryRider
{
    // Three identical riders approach from behind. Only the successful thief remains, using FoodThief's ownership.
    [DefaultExecutionOrder(100)]
    public sealed class CloneRiderHazard : MonoBehaviour, IPlacedHazard
    {
        private enum Phase { Ready, Approaching, Escaping, Spent }
        [SerializeField] private DeliverySession session;
        [SerializeField] private FoodThief[] clones = Array.Empty<FoodThief>();
        [SerializeField] private TMPro.TMP_Text callout;
        [SerializeField, Min(0.1f)] private float feedbackSeconds = 0.7f;
        [Header("복제 접근 — HTML 시험값")]
        [SerializeField, Min(0.1f)] private float triggerRadius = 10f;
        [SerializeField, Min(0.1f)] private float spawnBehindDistance = 14f;
        [SerializeField, Min(0f)] private float lateralSpacing = 1.4f;
        [SerializeField, Min(0.1f)] private float chaseSpeed = 7.6f;
        [SerializeField, Min(0.1f)] private float chaseSeconds = 6f;
        [SerializeField, Min(0.1f)] private float contactRadius = 1f;
        [SerializeField, Min(0.1f)] private float recoverySeconds = 1.5f;
        [Header("음식 도주 — FoodThief 회수·재수령")]
        [SerializeField, Min(0.1f)] private float escapeSpeed = 5.4f;
        [SerializeField, Min(1f)] private float escapeSeconds = 35f;
        [SerializeField, Min(0.5f)] private float escapePointSpacing = 3f;
        [SerializeField, Min(0.1f)] private float obstacleRadius = 0.45f;
        [SerializeField, Min(0.02f)] private float routeRetrySeconds = 0.25f;
        private RiderMotor rider;
        private HazardSlot slot;
        private RoadNetwork ground;
        private PursuitHazardPath path;
        private Phase phase;
        private object order;
        private float elapsed, escapeElapsed;
        private float nextAttemptAt;
        private float feedbackUntil;
        private Camera viewCamera;
        private Vector3 forward, routeEnd;
        private FoodThief thief;
        private Transform routeRoot;
        private readonly Transform[] route = new Transform[20];
        private readonly bool[] removed = new bool[3];
        private readonly Vector3[] spawnPositions = new Vector3[3];
        private float TriggerScale => slot != null ? slot.TriggerScale : 1f;

        public void Place(DeliverySession owner, RoadNetwork network, HazardCandidate spot)
        { session = owner; ground = network; }

        private void Awake()
        {
            rider = session != null && session.Player != null ? session.Player.GetComponent<RiderMotor>() : null;
            if (rider == null || clones.Length != 3 || Array.Exists(clones, c => c == null)) { enabled = false; return; }
            slot = GetComponent<HazardSlot>();
            viewCamera = Camera.main;
            path = new PursuitHazardPath(session, ground, transform);
            routeRoot = new GameObject("CloneEscapeRoute").transform;
            routeRoot.SetParent(transform, false);
            for (int i = 0; i < route.Length; i++)
            {
                route[i] = new GameObject("Point_" + i).transform;
                route[i].SetParent(routeRoot, false);
            }
            foreach (var clone in clones)
                foreach (var shape in clone.GetComponentsInChildren<Collider>(true))
                    foreach (var playerShape in rider.GetComponents<Collider>()) Physics.IgnoreCollision(shape, playerShape);
            Stop();
        }

        public void ShowWaitingPreview()
        {
            for(int i=0;i<clones.Length;i++)
            {
                clones[i].Rest();
                clones[i].transform.SetPositionAndRotation(path.Ground(transform.position+transform.right*((i-1)*lateralSpacing)),transform.rotation);
                clones[i].gameObject.SetActive(true);
            }
            SetCallout(false,"",Vector3.zero);
        }

        private void OnEnable()
        {
            if (rider == null) return;
            order = session.ActiveOrder;
            phase = Phase.Ready;
        }

        private void OnDisable() => Stop();

        private void FixedUpdate()
        {
            if (rider == null || session.IsPaused) return;
            if (order != session.ActiveOrder)
            { Stop(); order = session.ActiveOrder; phase = Phase.Ready; }
            if (session.IsSettled) { Stop(); return; }
            if (phase == Phase.Escaping) { Escape(); return; }
            if (session.IsMapOpen || !(slot != null ? slot.StrikeAllowed : HazardPlanner.DeliveryInProgress(session)))
            {
                if (phase == Phase.Approaching) Stop();
                return;
            }
            if (phase == Phase.Ready && Time.time >= nextAttemptAt && rider.IsGroundedForHazards && !rider.IsRecovering
                && Vector3.Distance(rider.transform.position, transform.position) < triggerRadius * TriggerScale)
            { nextAttemptAt = Time.time + routeRetrySeconds; Begin(); }
            else if (phase == Phase.Approaching) Approach();
        }

        private void Begin()
        {
            forward = Vector3.ProjectOnPlane(rider.HorizontalSpeed > 0.5f ? rider.Velocity : rider.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.01f) forward = transform.forward;
            var right = Vector3.Cross(Vector3.up, forward);
            for (int i = 0; i < clones.Length; i++)
            {
                spawnPositions[i] = path.Ground(rider.transform.position - forward * spawnBehindDistance + right * ((i - 1) * lateralSpacing));
                if (!path.CanStep(spawnPositions[i], rider.transform.position, obstacleRadius)) return;
            }
            for (int i = 0; i < clones.Length; i++)
            {
                var clone = clones[i];
                clone.Rest();
                // Disabled movement, live punch target: a decoy can be punched out before any theft.
                clone.enabled = false;
                clone.Initialize(session, route, chaseSpeed, chaseSeconds, false);
                var body = clone.GetComponent<Rigidbody>();
                body.isKinematic = true;
                // Activating an inactive Rigidbody registers its Transform pose with PhysX.
                // Set both: setting only body.position before activation loses the 14m spawn offset.
                clone.transform.SetPositionAndRotation(spawnPositions[i], Quaternion.LookRotation(forward));
                clone.gameObject.SetActive(true);
                body.position = spawnPositions[i]; body.rotation = Quaternion.LookRotation(forward);
                removed[i] = false;
            }
            elapsed = 0f; phase = Phase.Approaching;
            SetCallout(true, "!!!", rider.transform.position + Vector3.up * 2.5f);
        }

        private void Approach()
        {
            elapsed += Time.fixedDeltaTime;
            if (elapsed >= chaseSeconds) { Stop(); return; }
            int alive = 0;
            for (int i = 0; i < clones.Length; i++)
            {
                var clone = clones[i];
                if (removed[i]) continue;
                if (!clone.gameObject.activeSelf) { removed[i] = true; continue; }
                var before = clone.transform.position;
                var target = rider.transform.position;
                var delta = Vector3.ProjectOnPlane(target - before, Vector3.up);
                var next = path.Ground(before + delta.normalized * Mathf.Min(delta.magnitude, chaseSpeed * Time.fixedDeltaTime));
                if (!path.CanStep(before, next, obstacleRadius)) { clone.gameObject.SetActive(false); removed[i] = true; continue; }
                var body = clone.GetComponent<Rigidbody>();
                body.MovePosition(next);
                if (delta.sqrMagnitude > 0.0001f) body.MoveRotation(Quaternion.LookRotation(delta));
                alive++;
                if (!rider.IsGroundedForHazards || rider.IsRecovering || Vector3.Distance(target, next) >= contactRadius * TriggerScale) continue;
                if (Time.time >= nextAttemptAt)
                { nextAttemptAt = Time.time + routeRetrySeconds; if (Steal(clone)) return; }
            }
            if (alive == 0) Stop();
        }

        private bool Steal(FoodThief clone)
        {
            if (!BuildRoute(clone.transform.position) || !session.TryLoseFood(clone.transform)) return false;
            thief = clone;
            foreach (var other in clones) if (other != thief) other.gameObject.SetActive(false);
            var body = thief.GetComponent<Rigidbody>();
            body.isKinematic = false;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            thief.Initialize(session, route, escapeSpeed, escapeSeconds);
            thief.enabled = true;
            escapeElapsed = 0f; phase = Phase.Escaping;
            feedbackUntil = Time.time + feedbackSeconds;
            // HTML stun has no launch: reuse a physical tumble with zero additional velocity.
            rider.TryHazardCrash(this, Vector3.zero, recoverySeconds);
            SetCallout(true, "슈웅", thief.transform.position + Vector3.up * 2.2f);
            return true;
        }

        private bool BuildRoute(Vector3 start)
        {
            int best = 0;
            Vector3 bestDirection = forward;
            for (int turn = 0; turn < 8; turn++)
            {
                var direction = Quaternion.Euler(0f, turn * 45f, 0f) * forward;
                int count = 0;
                var previous = start;
                for (int i = 0; i < route.Length; i++)
                {
                    var point = path.Ground(start + direction * ((i + 1) * escapePointSpacing));
                    if (!path.CanStep(previous, point, obstacleRadius)) break;
                    count++; previous = point;
                }
                if (count <= best) continue;
                best = count; bestDirection = direction;
            }
            if (best < 2) return false;
            forward = bestDirection;
            for (int i = 0; i < route.Length; i++)
                route[i].position = path.Ground(start + forward * (Mathf.Min(i + 1, best) * escapePointSpacing));
            routeEnd = route[route.Length - 1].position;
            return true;
        }

        private void Escape()
        {
            if (thief == null || thief.Finished || !session.IsChasingFood || session.Target != thief.transform) { Stop(); return; }
            escapeElapsed += Time.fixedDeltaTime;
            var next = thief.transform.position + Vector3.ProjectOnPlane(thief.GetComponent<Rigidbody>().linearVelocity, Vector3.up) * Time.fixedDeltaTime;
            if (escapeElapsed >= escapeSeconds || !path.CanStep(thief.transform.position, next, obstacleRadius)) { Stop(); return; }
            if (Vector3.ProjectOnPlane(routeEnd - thief.transform.position, Vector3.up).sqrMagnitude < escapePointSpacing * escapePointSpacing * 4f)
            {
                float remaining = escapeSeconds - escapeElapsed;
                if (!BuildRoute(thief.transform.position)) { Stop(); return; }
                thief.Initialize(session, route, escapeSpeed, Mathf.Max(1f, remaining));
            }
            SetCallout(true, Time.time < feedbackUntil ? "슈웅" : "!", thief.transform.position + Vector3.up * 2.2f);
        }

        private void Stop()
        {
            if (thief != null && session != null) session.RequireReplacement(thief.transform);
            thief = null;
            foreach (var clone in clones)
            {
                if (clone == null) continue;
                clone.gameObject.SetActive(false);
                clone.Rest();
                clone.enabled = true;
                var body = clone.GetComponent<Rigidbody>();
                body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                body.isKinematic = true;
            }
            SetCallout(false, "", Vector3.zero);
            phase = Phase.Spent;
        }

        private void SetCallout(bool visible, string text, Vector3 point)
        {
            if (callout == null) return;
            callout.gameObject.SetActive(visible); callout.text = text; callout.transform.position = point;
            callout.color = new Color(1f, 0.54f, 0.16f);
            if (viewCamera != null) callout.transform.rotation = viewCamera.transform.rotation;
        }
    }
}
