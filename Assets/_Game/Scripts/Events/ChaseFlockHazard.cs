using UnityEngine;

namespace DeliveryRider
{
    // Free birds chase for seven seconds. Attached birds share a separate, extendable attachment timer.
    public sealed class ChaseFlockHazard : MonoBehaviour, IPlacedHazard
    {
        private enum Phase { Idle, Chasing, Spent }
        [SerializeField] private DeliverySession session;
        [SerializeField] private Transform[] birds = System.Array.Empty<Transform>();
        [Header("Test values")]
        [SerializeField, Min(0.1f)] private float triggerRadius = 7f;
        [SerializeField, Min(0.1f)] private float chaseSeconds = 7f;
        [Tooltip("Attachment duration when the first chicken attaches.")]
        [SerializeField, Min(0.1f)] private float attachmentSeconds = 2.5f;
        [Tooltip("Time added for each additional chicken while attached.")]
        [SerializeField, Min(0.1f)] private float extraAttachmentSeconds = 1.5f;
        [SerializeField, Min(0.1f)] private float chaseSpeed = 5.2f;
        [SerializeField, Min(0f)] private float riderSpeedShare = 1.15f;
        [SerializeField, Min(0.1f)] private float maximumChaseSpeed = 20f;
        [Header("Independent wandering")]
        [SerializeField] private Vector2 turnInterval = new Vector2(0.35f, 0.8f);
        [SerializeField, Min(0f)] private float wanderRadius = 2.5f;
        [SerializeField, Range(0f, 180f)] private float wanderAngle = 65f;
        [SerializeField, Min(0.1f)] private float separationDistance = 0.8f;
        [SerializeField, Min(0.1f)] private float contactRadius = 0.65f;
        [SerializeField, Min(.1f)] private float landingRadius = 2.6f;
        private RiderMotor rider;
        private CapsuleCollider playerShape;
        private RoadNetwork ground;
        private HazardSlot slot;
        private float TriggerScale => slot != null ? slot.TriggerScale : 1f;
        private Vector3[] homes, headings;
        private Quaternion[] facings;
        private float[] turnAt;
        private bool[] attached;
        private Phase phase;
        private float elapsed;
        private bool hasAttachedBirds;

        public void Place(DeliverySession owner, RoadNetwork roadGround, HazardCandidate spot)
        {
            session = owner;
            ground = roadGround;
        }

        private void Awake()
        {
            rider = session != null && session.Player != null ? session.Player.GetComponent<RiderMotor>() : null;
            if (rider == null || birds.Length == 0) { enabled = false; return; }
            playerShape = rider.GetComponent<CapsuleCollider>();
            slot = GetComponent<HazardSlot>();
            homes = new Vector3[birds.Length];
            facings = new Quaternion[birds.Length];
            headings = new Vector3[birds.Length];
            turnAt = new float[birds.Length];
            attached = new bool[birds.Length];
            for (int i = 0; i < birds.Length; i++)
            {
                if (birds[i] == null) { enabled = false; return; }
                homes[i] = birds[i].localPosition;
                facings[i] = birds[i].localRotation;
            }
        }

        private void OnEnable()
        {
            if (homes == null || rider == null) return;
            rider.ReactionChanged += OnReaction;
            rider.SafePositionRestored += OnSafePositionRestored;
            ResetBirds();
            phase = Phase.Idle;
        }

        private void OnDisable()
        {
            if (rider != null)
            {
                rider.ReactionChanged -= OnReaction;
                rider.SafePositionRestored -= OnSafePositionRestored;
            }
            Finish();
        }

        private void OnReaction(RiderReactionState state)
        {
            if (hasAttachedBirds && state != RiderReactionState.Normal) Finish();
        }

        private void OnSafePositionRestored()
        {
            if (hasAttachedBirds) Finish();
        }

        private void Update()
        {
            if (rider != null && session.IsSettled && phase == Phase.Chasing) Finish();
        }

        private void FixedUpdate()
        {
            if (rider == null || session.IsPaused || session.IsSettled) return;
            switch (phase)
            {
                case Phase.Idle:
                    if ((slot == null || slot.StrikeAllowed) && !session.IsMapOpen && !rider.IsRecovering && Near(transform.position, triggerRadius))
                    {
                        elapsed = 0f;
                        for (int i = 0; i < birds.Length; i++) turnAt[i] = 0f;
                        phase = Phase.Chasing;
                    }
                    break;
                case Phase.Chasing:
                    Chase();
                    break;
                case Phase.Spent:
                    if (!Near(transform.position, triggerRadius * 3f))
                    {
                        ResetBirds();
                        phase = Phase.Idle;
                    }
                    break;
            }
        }

        private float NextTurnDelay() => Random.Range(Mathf.Max(0.1f, turnInterval.x), Mathf.Max(0.1f, Mathf.Max(turnInterval.x, turnInterval.y)));

        private void Chase()
        {
            elapsed += Time.fixedDeltaTime;
            if (session.IsMapOpen) { Finish(); return; }
            if (elapsed >= chaseSeconds)
            {
                for (int i = 0; i < birds.Length; i++) if (!attached[i]) birds[i].gameObject.SetActive(false);
                if (!hasAttachedBirds) { Finish(); return; }
            }
            bool mayStrike = slot == null || slot.StrikeAllowed;
            // Completion ends attachment; recovering dropped food remains part of delivery.
            if (hasAttachedBirds && !mayStrike) EndAttachedBirds();
            if (hasAttachedBirds)
            {
                if (!rider.HasChickenAttachment(this))
                {
                    EndAttachedBirds();
                    if (elapsed >= chaseSeconds) { Finish(); return; }
                }
            }
            var feet = rider.transform.position;
            feet.y = playerShape != null ? playerShape.bounds.min.y : feet.y - 1f;
            float speed = Mathf.Clamp(rider.HorizontalSpeed * riderSpeedShare, chaseSpeed, Mathf.Max(chaseSpeed, maximumChaseSpeed));
            for (int i = 0; i < birds.Length; i++)
            {
                if (attached[i] || !birds[i].gameObject.activeSelf) continue;
                var before = birds[i].position;
                if (elapsed >= turnAt[i])
                {
                    float angle = i * Mathf.PI * 2f / birds.Length + Random.Range(-0.6f, 0.6f);
                    var target = feet + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * wanderRadius;
                    var seek = Vector3.ProjectOnPlane(target - before, Vector3.up).normalized;
                    headings[i] = Quaternion.Euler(0f, Random.Range(-wanderAngle, wanderAngle), 0f) * seek;
                    turnAt[i] = elapsed + NextTurnDelay();
                }
                var separation = Vector3.zero;
                for (int j = 0; j < birds.Length; j++)
                {
                    if (j == i || attached[j] || !birds[j].gameObject.activeSelf) continue;
                    var away = Vector3.ProjectOnPlane(before - birds[j].position, Vector3.up);
                    float distance = away.magnitude;
                    if (distance > 0.001f && distance < separationDistance)
                        separation += away / distance * (1f - distance / separationDistance);
                }
                var step = (headings[i] + separation * 1.5f).normalized * (speed * Time.fixedDeltaTime);
                var position = before + step;
                if (ground != null) position = ground.OnGround(position, 0f);
                else position.y = feet.y;
                birds[i].position = position;
                if (step.sqrMagnitude > 0.0001f) birds[i].rotation = Quaternion.LookRotation(step);
                // Sweep the travelled segment so fast birds can touch the rider between fixed frames.
                var flatStep = Vector3.ProjectOnPlane(position - before, Vector3.up);
                float t = flatStep.sqrMagnitude > 0.0001f ? Mathf.Clamp01(Vector3.Dot(feet - before, flatStep) / flatStep.sqrMagnitude) : 0f;
                if (mayStrike && Near(Vector3.Lerp(before, position, t), rider.Velocity.y < -.1f ? Mathf.Max(contactRadius, landingRadius) : contactRadius) && !rider.IsRecovering)
                {
                    if (rider.TryAttachChicken(this, attachmentSeconds, extraAttachmentSeconds)) { attached[i] = true; hasAttachedBirds = true; }
                }
            }
        }

        private void LateUpdate()
        {
            if (!hasAttachedBirds || playerShape == null) return;
            var head = playerShape.bounds.center + Vector3.up * (playerShape.bounds.extents.y + 0.03f);
            int count = 0;
            for (int i = 0; i < birds.Length; i++) if (attached[i]) count++;
            int index = 0;
            for (int i = 0; i < birds.Length; i++)
            {
                if (!attached[i]) continue;
                float angle = index++ * Mathf.PI * 2f / count;
                var offset = count == 1 ? Vector3.zero : new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 0.25f;
                birds[i].SetPositionAndRotation(head + offset, rider.transform.rotation);
            }
        }

        private bool Near(Vector3 point, float radius)
        {
            var offset = rider.transform.position - point;
            radius *= TriggerScale;
            return Mathf.Abs(offset.y) < 2.5f * TriggerScale && new Vector2(offset.x, offset.z).sqrMagnitude <= radius * radius;
        }

        private void ResetBirds()
        {
            for (int i = 0; i < birds.Length; i++)
            {
                attached[i] = false;
                birds[i].localPosition = homes[i];
                birds[i].localRotation = facings[i];
                birds[i].gameObject.SetActive(true);
            }
        }

        private void EndAttachedBirds()
        {
            if (rider != null) rider.EndChickenAttachment(this);
            hasAttachedBirds = false;
            if (attached == null) return;
            for (int i = 0; i < birds.Length; i++)
            {
                if (!attached[i]) continue;
                attached[i] = false;
                if (birds[i] != null) birds[i].gameObject.SetActive(false);
            }
        }

        private void Finish()
        {
            EndAttachedBirds();
            if (attached != null)
                for (int i = 0; i < birds.Length; i++)
                {
                    attached[i] = false;
                    if (birds[i] != null) birds[i].gameObject.SetActive(false);
                }
            phase = Phase.Spent;
        }
    }
}
