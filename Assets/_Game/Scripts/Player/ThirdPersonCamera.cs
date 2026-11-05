using UnityEngine;

namespace DeliveryRider
{
    [RequireComponent(typeof(Camera))]
    public sealed class ThirdPersonCamera : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private RiderMotor motor;
        [SerializeField] private float distance = 7f;
        [SerializeField] private float sensitivity = 0.12f;
        [SerializeField] private float pitch = 26f;
        [Tooltip("Mouse look keeps the pitch between these, in degrees (0 level, 90 straight down from above).")]
        [SerializeField, Range(-20f, 45f)] private float minimumPitch = 12f;
        [SerializeField, Range(30f, 89f)] private float maximumPitch = 65f;
        [SerializeField] private float targetHeight = 1f;
        [SerializeField] private LayerMask obstructionMask = 1;
        [Header("Speed and flight (test values)")]
        [SerializeField] private Vector2 fovSpeedRange = new Vector2(3f, 20f);
        [SerializeField, Range(0f, 30f)] private float maximumFovIncrease = 12f;
        [SerializeField, Min(0.01f)] private float fovSmoothTime = 0.2f;
        [SerializeField, Min(0f)] private float flightDistanceIncrease = 5f;
        [SerializeField, Min(0.1f)] private float flightHeightRange = 6f;
        [SerializeField, Min(0.01f)] private float followSmoothTime = 0.1f;
        [SerializeField, Min(0.01f)] private float distanceSmoothTime = 0.25f;
        [Header("Impact (zero amplitude disables shake)")]
        [SerializeField, Range(0f, 0.5f)] private float shakeAmplitude = 0.12f;
        [SerializeField, Min(0.01f)] private float shakeDuration = 0.18f;
        [SerializeField, Min(0.1f)] private float fullShakeImpulse = 25f;
        [Header("Collision and recovery")]
        [SerializeField, Min(0.01f)] private float collisionRadius = 0.22f;
        [SerializeField, Min(0f)] private float collisionPadding = 0.15f;
        [SerializeField, Min(1f)] private float groundProbeDistance = 100f;
        [SerializeField, Min(1f)] private float teleportDistance = 12f;
        private readonly RaycastHit[] hits = new RaycastHit[32];
        private readonly Collider[] overlaps = new Collider[32];
        private Camera lens;
        private float yaw, baseFov, fovVelocity, distanceVelocity, currentDistance;
        private float groundHeight, shakeRemaining, shakeStrength;
        private Vector3 focus, focusVelocity, lastTarget;
        private bool initialized;

        // The first view is the way the camera is saved in the scene, so a map can aim it past what stands behind the start.
        private void Awake() { lens = GetComponent<Camera>(); baseFov = lens.fieldOfView; yaw = transform.eulerAngles.y; }
        private void OnEnable()
        {
            initialized = false;
            if (motor == null) return;
            motor.ContactImpact += OnImpact;
            motor.SafePositionRestored += ResetTracking;
        }
        private void OnDisable()
        {
            if (motor != null)
            {
                motor.ContactImpact -= OnImpact;
                motor.SafePositionRestored -= ResetTracking;
            }
            shakeRemaining = 0f;
            if (lens != null) lens.fieldOfView = baseFov;
        }
        private void ResetTracking() { initialized = false; shakeRemaining = 0f; }
        private void OnImpact(float impulse)
        {
            if (!motor.CanLook) return;
            shakeStrength = Mathf.Max(shakeRemaining > 0f ? shakeStrength : 0f, Mathf.Clamp01(impulse / Mathf.Max(0.1f, fullShakeImpulse)));
            shakeRemaining = shakeDuration;
        }

        private void LateUpdate()
        {
            if (target == null || motor == null || lens == null) return;
            if (!motor.CanLook) shakeRemaining = 0f;
            if (motor.CameraFrozen) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            var position = target.position;
            bool reset = !initialized || (position - lastTarget).sqrMagnitude > teleportDistance * teleportDistance;
            if (reset)
            {
                focus = position + Vector3.up * targetHeight;
                focusVelocity = Vector3.zero;
                currentDistance = distance;
                distanceVelocity = fovVelocity = 0f;
                groundHeight = position.y;
                shakeRemaining = 0f;
                initialized = true;
            }
            lastTarget = position;
            UpdateGround(position);
            var look = motor.CameraLook;
            yaw += look.x * sensitivity;
            pitch = Mathf.Clamp(pitch - look.y * sensitivity, minimumPitch, Mathf.Max(minimumPitch, maximumPitch));
            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            var desiredFocus = position + Vector3.up * targetHeight;
            focus = Vector3.SmoothDamp(focus, desiredFocus, ref focusVelocity, followSmoothTime, Mathf.Infinity, dt);
            // Keep the smoothed focus from lagging through a wall.
            var offset = focus - desiredFocus;
            focus = desiredFocus + offset.normalized * ClearDistance(desiredFocus, offset);
            if (IsBlocked(focus)) { focus = desiredFocus; focusVelocity = Vector3.zero; }
            float air = motor.IsAccidentAirborne ? Mathf.Clamp01((position.y - groundHeight) / Mathf.Max(0.1f, flightHeightRange)) : 0f;
            float desiredDistance = distance + air * flightDistanceIncrease;
            var back = rotation * Vector3.back;
            float safeDistance = ClearDistance(focus, back * desiredDistance);
            if (safeDistance < currentDistance) { currentDistance = safeDistance; distanceVelocity = 0f; }
            else currentDistance = Mathf.SmoothDamp(currentDistance, safeDistance, ref distanceVelocity, distanceSmoothTime, Mathf.Infinity, dt);
            var candidate = back * currentDistance;
            if (shakeRemaining > 0f)
            {
                shakeRemaining = Mathf.Max(0f, shakeRemaining - dt);
                float amplitude = shakeAmplitude * shakeStrength * (shakeRemaining / Mathf.Max(0.01f, shakeDuration))
                    * Mathf.Clamp01(currentDistance / Mathf.Max(0.1f, distance));
                candidate += rotation * new Vector3(Mathf.Sin(Time.time * 113f), Mathf.Sin(Time.time * 157f), 0f) * amplitude;
            }
            // Check the final shaken position, not just the unshaken boom.
            candidate = candidate.normalized * ClearDistance(focus, candidate);
            // Sweep queries do not report a collider containing the origin. Fail closed there.
            if (IsBlocked(focus)) candidate = Vector3.zero;
            transform.SetPositionAndRotation(focus + candidate, rotation);
            float speedFactor = Mathf.InverseLerp(fovSpeedRange.x, fovSpeedRange.y, motor.HorizontalSpeed);
            lens.fieldOfView = Mathf.SmoothDamp(lens.fieldOfView, Mathf.Clamp(baseFov + maximumFovIncrease * speedFactor, 1f, 179f),
                ref fovVelocity, fovSmoothTime, Mathf.Infinity, dt);
        }

        private bool IsTarget(Collider collider) => collider.transform == target || collider.transform.IsChildOf(target);
        private bool IsBlocked(Vector3 position)
        {
            int count = Physics.OverlapSphereNonAlloc(position, collisionRadius, overlaps, obstructionMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++) if (!IsTarget(overlaps[i])) return true;
            return count == overlaps.Length;
        }
        private float ClearDistance(Vector3 origin, Vector3 offset)
        {
            float length = offset.magnitude;
            if (length <= 0.0001f) return 0f;
            int count = Physics.SphereCastNonAlloc(origin, collisionRadius, offset / length, hits, length,
                obstructionMask, QueryTriggerInteraction.Ignore);
            float clear = length;
            for (int i = 0; i < count; i++)
                if (!IsTarget(hits[i].collider)) clear = Mathf.Min(clear, Mathf.Max(0f, hits[i].distance - collisionPadding));
            return clear;
        }
        private void UpdateGround(Vector3 position)
        {
            int count = Physics.RaycastNonAlloc(position + Vector3.up * 0.1f, Vector3.down, hits, groundProbeDistance,
                obstructionMask, QueryTriggerInteraction.Ignore);
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                var hit = hits[i];
                if (IsTarget(hit.collider) || hit.rigidbody != null || hit.normal.y < 0.5f || hit.distance >= nearest) continue;
                nearest = hit.distance;
                groundHeight = hit.point.y;
            }
        }
    }
}
