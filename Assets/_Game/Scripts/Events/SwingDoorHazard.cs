using UnityEngine;

namespace DeliveryRider
{
    public enum DoorRearmPolicy { NextOrder, AfterExitDelay }

    // The approach box swings the door open at once (no warning delay: at running speed the rider would be past it);
    // only the swept door panel can cause a hit.
    public sealed class SwingDoorHazard : MonoBehaviour, IPlacedHazard
    {
        public event System.Action ContactHit;
        private enum Phase { Ready, Opening, Holding, Closing, Spent }
        [SerializeField] private DeliverySession session;
        [SerializeField] private Transform hinge;
        [SerializeField] private BoxCollider panel;
        [SerializeField] private Renderer warningLamp;
        [SerializeField] private AudioSource speaker;
        [SerializeField] private Vector3 approachCenter = new Vector3(1.3f, 1, 1.2f);
        [SerializeField] private Vector3 approachSize = new Vector3(3.2f, 3, 5);
        [SerializeField, Min(0.1f)] private float openingSeconds = 0.45f;
        [SerializeField, Range(10, 120)] private float openingAngle = 90;
        [SerializeField, Min(0.1f)] private float holdSeconds = 1.2f;
        [SerializeField, Min(0.1f)] private float closingSeconds = 1;
        [SerializeField, Min(0)] private float horizontalImpulse = 6;
        [SerializeField, Min(0)] private float upwardImpulse = 2.5f;
        [SerializeField, Min(0.1f)] private float recoverySeconds = 1.1f;
        [SerializeField] private DoorRearmPolicy rearmPolicy = DoorRearmPolicy.NextOrder;
        [SerializeField, Min(0)] private float rearmSeconds = 8;
        [SerializeField, Min(1)] private float rearmDistance = 4;
        private RiderMotor rider;
        private CapsuleCollider playerShape;
        private Quaternion closedRotation = Quaternion.identity;
        private bool initialized;
        private Phase phase;
        private float elapsed;
        private float angle;
        private int activatedOrder;
        private bool hitThisCycle;
        private HazardSlot slot;
        private float TriggerScale => slot != null ? slot.TriggerScale : 1f;
        private AudioClip warningSound;
        private AudioClip hitSound;

        public void Place(DeliverySession owner, RoadNetwork ground, HazardCandidate spot) => session = owner;

        private void Awake()
        {
            if (session == null || session.Player == null || hinge == null || panel == null || panel.transform != hinge)
            {
                Debug.LogError("Door requires session, hinge and a BoxCollider on the hinge.", this);
                enabled = false;
                return;
            }
            rider = session.Player.GetComponent<RiderMotor>();
            playerShape = session.Player.GetComponent<CapsuleCollider>();
            if (rider == null || playerShape == null) { enabled = false; return; }
            closedRotation = hinge.localRotation;
            slot = GetComponent<HazardSlot>();
            initialized = true;
            panel.isTrigger = true; // The visual door cannot pin the player against a wall.
            warningSound = Tone("Door warning", 660, 0.16f);
            hitSound = Tone("Door hit", 130, 0.12f);
            SetLamp(false);
        }

        private void Update()
        {
            if (session != null && session.IsSettled)
            { ResetPose(); phase = Phase.Spent; if (speaker != null) speaker.Stop(); }
        }

        private void FixedUpdate()
        {
            if (session == null || session.IsPaused) return;
            if (session.IsSettled) { ResetPose(); phase = Phase.Spent; return; }
            // Opening a map disarms this swing; resuming never applies a hidden delayed hit.
            if (session.IsMapOpen && phase != Phase.Ready && phase != Phase.Spent)
            { hitThisCycle = true; SetLamp(false); }
            elapsed += Time.fixedDeltaTime;
            switch (phase)
            {
                case Phase.Ready:
                    if (!session.IsMapOpen && !rider.IsRecovering && (slot == null || slot.StrikeAllowed) && InApproach())
                    {
                        activatedOrder = session.ActiveOrder != null ? session.ActiveOrder.Number : 0;
                        hitThisCycle = false;
                        Enter(Phase.Opening);
                        SetLamp(true);
                        Play(warningSound);
                    }
                    break;
                case Phase.Opening:
                    float next = Mathf.Lerp(0, openingAngle, Mathf.Clamp01(elapsed / openingSeconds));
                    Sweep(angle, next);
                    angle = next;
                    SetAngle(angle);
                    if (elapsed >= openingSeconds) { Enter(Phase.Holding); SetLamp(false); }
                    break;
                case Phase.Holding:
                    if (elapsed >= holdSeconds) Enter(Phase.Closing);
                    break;
                case Phase.Closing:
                    angle = Mathf.Lerp(openingAngle, 0, Mathf.Clamp01(elapsed / closingSeconds));
                    SetAngle(angle); // Closing is harmless so it cannot trap someone returning.
                    if (elapsed >= closingSeconds) { ResetPose(); Enter(Phase.Spent); }
                    break;
                case Phase.Spent:
                    bool nextOrder = session.ActiveOrder != null && session.ActiveOrder.Number != activatedOrder;
                    bool rearm = rearmPolicy == DoorRearmPolicy.NextOrder ? nextOrder : elapsed >= rearmSeconds;
                    if (rearm && !InApproach() && Vector3.Distance(rider.transform.position, hinge.position) > rearmDistance)
                        Enter(Phase.Ready);
                    break;
            }
        }

        private bool InApproach()
        {
            Vector3 p = transform.InverseTransformPoint(rider.transform.position) - approachCenter;
            Vector3 half = approachSize * (0.5f * TriggerScale);
            return Mathf.Abs(p.x) <= half.x && Mathf.Abs(p.y) <= half.y && Mathf.Abs(p.z) <= half.z;
        }

        private void Sweep(float from, float to)
        {
            if (hitThisCycle || session.IsMapOpen) return;
            // Subdivide the swing; a thin fast panel must not skip the player's capsule.
            int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(to - from) / 2));
            for (int i = 0; i <= steps; i++)
            {
                float a = Mathf.Lerp(from, to, (float)i / steps);
                var parentRotation = hinge.parent != null ? hinge.parent.rotation : Quaternion.identity;
                var rotation = parentRotation * closedRotation * Quaternion.Euler(0, a, 0);
                if (!Physics.ComputePenetration(panel, hinge.position, rotation, playerShape,
                    playerShape.transform.position, playerShape.transform.rotation, out _, out _)) continue;
                Vector3 radial = Vector3.ProjectOnPlane(rider.transform.position - hinge.position, transform.up).normalized;
                Vector3 push = Vector3.ProjectOnPlane(Vector3.Cross(transform.up, radial), Vector3.up).normalized;
                if (push.sqrMagnitude < 0.01f) push = transform.right;
                // Consume the contact even if another effect rejects it; don't hit later in the same swing.
                hitThisCycle = true;
                if (rider.TryHazardCrash(this, push * horizontalImpulse + Vector3.up * upwardImpulse, recoverySeconds))
                {
                    Play(hitSound);
                    ContactHit?.Invoke();
                }
                break;
            }
        }

        private void Enter(Phase value) { phase = value; elapsed = 0; }
        private void SetAngle(float value) => hinge.localRotation = closedRotation * Quaternion.Euler(0, value, 0);
        private void SetLamp(bool on) { if (warningLamp != null) warningLamp.enabled = on; }
        private void ResetPose() { angle = 0; if (initialized && hinge != null) SetAngle(0); SetLamp(false); }
        private void Play(AudioClip clip) { if (speaker != null && clip != null) speaker.PlayOneShot(clip, 0.3f); }
        private void OnDisable() { ResetPose(); if (speaker != null) speaker.Stop(); }
        private void OnDestroy()
        {
            if (warningSound != null) Destroy(warningSound);
            if (hitSound != null) Destroy(hitSound);
        }

        private static AudioClip Tone(string name, float frequency, float seconds)
        {
            const int rate = 22050;
            var samples = new float[Mathf.CeilToInt(rate * seconds)];
            for (int i = 0; i < samples.Length; i++)
            {
                float t = (float)i / rate;
                float envelope = Mathf.Min(1, t / 0.008f) * (1 - (float)i / samples.Length);
                samples[i] = Mathf.Sin(t * frequency * Mathf.PI * 2) * envelope * 0.4f;
            }
            var clip = AudioClip.Create(name, samples.Length, 1, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(approachCenter, approachSize);
        }
    }
}
