using UnityEngine;

namespace DeliveryRider
{
    // Runs after RiderMotor so its normal recovery, closed-zone checks and collision response always run first.
    // The existing owned slip supplies controls/chain hits; only this incident's velocity is changed.
    [DefaultExecutionOrder(100)]
    public sealed class DogHazard : MonoBehaviour, IPlacedHazard, IPunchable
    {
        private enum Phase { Ready, Chasing, Dashing, Spent }
        [SerializeField] private DeliverySession session;
        [SerializeField] private Transform dog;
        [SerializeField] private Transform[] legs = System.Array.Empty<Transform>();
        [SerializeField] private Transform tail;
        [SerializeField] private TMPro.TMP_Text callout;
        [SerializeField, Min(0.1f)] private float feedbackSeconds = 0.7f;
        [Header("개 추격 — HTML 시험값")]
        [SerializeField, Min(0.1f)] private float triggerRadius = 14f;
        [SerializeField, Min(0.1f)] private float chaseSpeed = 8.4f;
        [SerializeField, Min(0.1f)] private float turnRate = 2f;
        [SerializeField, Min(0.1f)] private float chaseSeconds = 9f;
        [SerializeField, Min(0.1f)] private float biteRadius = 1f;
        [Header("물려서 질주 — 속도 대신 직렬화한 배율·거리")]
        [SerializeField, Min(0.1f)] private float dashDistance = 30f;
        [SerializeField, Min(1f)] private float dashSpeedMultiplier = 1.55f;
        [SerializeField, Min(0f)] private float dashSteering = 1.1f;
        [SerializeField, Min(0.1f)] private float obstacleRadius = 0.45f;
        [Tooltip("Keep HTML horizontal speed 5; only vertical velocity scales by 9.81/22 to preserve flight time and distance.")]
        [SerializeField, Min(0f)] private float bounceSpeed = 5f;
        [SerializeField, Min(0f)] private float bounceLift = 9f;
        private RiderMotor rider;
        private Rigidbody playerBody;
        private HazardSlot slot;
        private RoadNetwork ground;
        private PursuitHazardPath path;
        private Phase phase;
        private object order;
        private float elapsed, travelled, dashSpeed;
        private float feedbackUntil;
        private Camera viewCamera;
        private Vector3 heading, previous, home;
        private Quaternion homeRotation;
        private float TriggerScale => slot != null ? slot.TriggerScale : 1f;

        public void Place(DeliverySession owner, RoadNetwork network, HazardCandidate spot)
        { session = owner; ground = network; }

        private void Awake()
        {
            rider = session != null && session.Player != null ? session.Player.GetComponent<RiderMotor>() : null;
            if (rider == null || dog == null) { enabled = false; return; }
            playerBody = rider.GetComponent<Rigidbody>();
            slot = GetComponent<HazardSlot>();
            viewCamera = Camera.main;
            path = new PursuitHazardPath(session, ground, transform);
            home = dog.localPosition;homeRotation=dog.localRotation;
            foreach (var shape in dog.GetComponentsInChildren<Collider>(true))
                foreach (var playerShape in rider.GetComponents<Collider>()) Physics.IgnoreCollision(shape, playerShape);
            dog.gameObject.SetActive(false);
        }

        private void OnEnable()
        {
            if (rider == null) return;
            dog.SetLocalPositionAndRotation(home,homeRotation);
            elapsed=travelled=0f;feedbackUntil=0f;
            rider.SafePositionRestored += Stop;
            rider.HazardHit += OnHazardHit;
            order = session.ActiveOrder;
            phase = Phase.Ready;
        }

        private void OnDisable()
        {
            if (rider != null)
            { rider.SafePositionRestored -= Stop; rider.HazardHit -= OnHazardHit; }
            Stop();
        }

        private void FixedUpdate()
        {
            if (rider == null || session.IsPaused) return;
            if (order != session.ActiveOrder)
            {
                Stop(); order = session.ActiveOrder; phase = Phase.Ready; dog.localPosition = home;
            }
            bool allowed = !session.IsSettled && !session.IsMapOpen && (slot != null ? slot.StrikeAllowed : HazardPlanner.DeliveryInProgress(session));
            if (!allowed)
            {
                if (phase == Phase.Chasing || phase == Phase.Dashing) Stop();
                dog.gameObject.SetActive(false);
                return;
            }
            if (phase == Phase.Ready)
            {
                dog.gameObject.SetActive(true);
                if (!rider.IsRecovering && Vector3.Distance(rider.transform.position, dog.position) < triggerRadius * TriggerScale)
                {
                    heading = Vector3.ProjectOnPlane(rider.transform.position - dog.position, Vector3.up).normalized;
                    if (heading.sqrMagnitude < 0.01f) heading = transform.forward;
                    elapsed = 0f; phase = Phase.Chasing;
                }
            }
            else if (phase == Phase.Chasing) Chase();
            else if (phase == Phase.Dashing) Dash();
            Animate();
        }

        private void Chase()
        {
            elapsed += Time.fixedDeltaTime;
            if (elapsed >= chaseSeconds) { Stop(); return; }
            var desired = Vector3.ProjectOnPlane(rider.transform.position - dog.position, Vector3.up).normalized;
            heading = Vector3.RotateTowards(heading, desired, turnRate * Time.fixedDeltaTime, 0f);
            var next = path.Ground(dog.position + heading * (chaseSpeed * Time.fixedDeltaTime));
            if (!path.CanStep(dog.position, next, obstacleRadius)) { Stop(); return; }
            dog.SetPositionAndRotation(next, Quaternion.LookRotation(heading));
            if (!rider.IsGroundedForHazards || Vector3.Distance(rider.transform.position, dog.position) >= biteRadius * TriggerScale) return;
            dashSpeed = rider.Speed * dashSpeedMultiplier;
            if (!rider.TryHazardSlip(this, heading, dashDistance / Mathf.Max(0.1f, dashSpeed) + 1f)) return;
            travelled = elapsed = 0f; previous = playerBody.position;
            phase = Phase.Dashing;
            feedbackUntil = Time.time + feedbackSeconds;
            dog.gameObject.SetActive(false);
            SetVelocity(heading * dashSpeed);
        }

        private void Dash()
        {
            elapsed += Time.fixedDeltaTime;
            if (!rider.IsSlipping || playerBody.isKinematic || rider.IsRecovering) { Stop(); return; }
            travelled += Vector3.ProjectOnPlane(playerBody.position - previous, Vector3.up).magnitude;
            previous = playerBody.position;
            if (travelled >= dashDistance || elapsed > dashDistance / Mathf.Max(0.1f, dashSpeed) + 0.5f) { Stop(); return; }
            var drift = Vector3.ProjectOnPlane(playerBody.linearVelocity, Vector3.up);
            heading = Vector3.RotateTowards(heading, drift.sqrMagnitude > 0.01f ? drift.normalized : heading,
                dashSteering * Time.fixedDeltaTime, 0f).normalized;
            var next = playerBody.position + heading * (dashSpeed * Time.fixedDeltaTime);
            if (!path.CanStep(playerBody.position, next, obstacleRadius))
            {
                Stop();
                rider.TryTrafficRelaunch(-heading * bounceSpeed + Vector3.up * bounceLift, 0.4f, 0.1f);
                return;
            }
            SetVelocity(heading * dashSpeed);
        }

        private void SetVelocity(Vector3 velocity)
            => playerBody.linearVelocity = new Vector3(velocity.x, playerBody.linearVelocity.y, velocity.z);

        public bool ReceivePunch(Vector3 direction)
        {
            if (phase != Phase.Chasing) return false;
            Stop();
            return true;
        }

        private void OnHazardHit(UnityEngine.Object source)
        {
            if (source != this && phase == Phase.Dashing) Stop();
        }

        private void Stop()
        {
            if (rider != null)
            {
                bool owned = phase == Phase.Dashing && rider.IsSlipping;
                rider.CancelSlip(this);
                if (owned && !rider.IsSlipping && playerBody != null && !playerBody.isKinematic)
                    SetVelocity(Vector3.ClampMagnitude(Vector3.ProjectOnPlane(playerBody.linearVelocity, Vector3.up), rider.Speed));
            }
            if (dog != null) dog.gameObject.SetActive(false);
            if (callout != null) callout.gameObject.SetActive(false);
            phase = Phase.Spent;
        }

        private void Animate()
        {
            if (tail != null) tail.localRotation = Quaternion.Euler(0f, Mathf.Sin(Time.time * 16f) * 40f, 0f);
            for (int i = 0; i < legs.Length; i++)
                if (legs[i] != null) legs[i].localRotation = Quaternion.Euler(phase == Phase.Chasing ? Mathf.Sin(Time.time * 18f + (i == 0 || i == 3 ? 0f : Mathf.PI)) * 50f : 0f, 0f, 0f);
            if (callout != null)
            {
                bool bitten = phase == Phase.Dashing && Time.time < feedbackUntil;
                callout.gameObject.SetActive(phase == Phase.Chasing || bitten);
                callout.text = bitten ? "멍!" : "!";
                callout.color = new Color(0.72f, 0.51f, 0.31f);
                callout.transform.position = (bitten ? rider.transform.position : dog.position) + Vector3.up * 1.8f;
                if (viewCamera != null) callout.transform.rotation = viewCamera.transform.rotation;
            }
        }
    }
}
