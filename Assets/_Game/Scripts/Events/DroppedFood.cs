using UnityEngine;

namespace DeliveryRider
{
    // Food knocked loose by a hazard: it arcs to the road and comes back when the rider walks over it.
    // The session accepts the loss only once the rider is back on their feet, so the arc starts first.
    public sealed class DroppedFood : MonoBehaviour
    {
        // Rider's centre stands about a metre above the road; matches a low pass over the food.
        private const float PickupHeight = 2.1f;
        private DeliverySession session;
        private Transform rider;
        private Vector3 from, landing;
        private float elapsed, flightSeconds, peak, pickupRadius;
        private bool lost;

        // The session treats dropped food like a thief to chase; the on-screen guidance tells the two apart.
        public static bool IsTarget(DeliverySession session)
            => session.IsChasingFood && session.Target.TryGetComponent<DroppedFood>(out _);

        public void Launch(DeliverySession owner, Transform player, Vector3 start, Vector3 restingPoint, float upSpeed, float radius, GameObject look)
        {
            session = owner;
            rider = player;
            from = start;
            landing = restingPoint;
            pickupRadius = radius;
            float scale = player.TryGetComponent<RiderMotor>(out var motor) ? motor.AccidentGravityScale : 1f;
            float gravity = Mathf.Max(0.01f, -Physics.gravity.y * scale);
            flightSeconds = Mathf.Max(0.1f, 2f * upSpeed / gravity);
            peak = upSpeed * upSpeed / (2f * gravity);
            transform.position = start;
            if (look == null) return;
            var visual = Instantiate(look, transform);
            visual.transform.localPosition = Vector3.zero;
            foreach (var collider in visual.GetComponentsInChildren<Collider>(true)) Destroy(collider);
            visual.SetActive(true);
        }

        private void Update()
        {
            if (session == null || session.IsSettled) { Destroy(gameObject); return; }
            if (!lost)
            {
                if (session.Stage != DeliveryStage.Carrying) { Destroy(gameObject); return; }
                lost = session.TryLoseFood(transform);
            }
            else if (!session.IsChasingFood || session.Target != transform) { Destroy(gameObject); return; }
            if (session.IsPaused) return;
            if (elapsed < flightSeconds)
            {
                elapsed += Time.deltaTime;
                float k = Mathf.Clamp01(elapsed / flightSeconds);
                transform.position = Vector3.Lerp(from, landing, k) + Vector3.up * (4f * peak * k * (1f - k));
                return;
            }
            if (!lost) return;
            var offset = rider.position - transform.position;
            if (Mathf.Abs(offset.y) <= PickupHeight && new Vector2(offset.x, offset.z).sqrMagnitude <= pickupRadius * pickupRadius
                && session.TryRecoverFood(transform)) Destroy(gameObject);
        }

        // Anything removing the food while it is still the target falls back to re-collecting at the shop.
        private void OnDestroy()
        {
            if (session != null) session.RequireReplacement(transform);
        }
    }
}
