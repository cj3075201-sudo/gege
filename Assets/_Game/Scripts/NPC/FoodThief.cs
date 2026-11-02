using UnityEngine;

namespace DeliveryRider
{
    [RequireComponent(typeof(Rigidbody))]
    public sealed class FoodThief : MonoBehaviour, IPunchable
    {
        private Rigidbody body;
        private DeliverySession session;
        private Vector3[] route;
        private int waypoint;
        private float speed;
        private double expiresAt;
        private bool carryingFood;
        public bool Finished { get; private set; }
        private void Awake() => body = GetComponent<Rigidbody>();
        public void Rest()
        {
            session = null;
            route = null;
            carryingFood = false;
            Finished = true;
            if (body != null && !body.isKinematic) body.linearVelocity = body.angularVelocity = Vector3.zero;
            var bag = transform.Find("StolenFood");
            if (bag != null) bag.gameObject.SetActive(false);
        }
        public void Initialize(DeliverySession owner, Transform[] points, float moveSpeed, float lifetime, bool hasStolenFood = true)
        {
            session = owner;
            carryingFood = hasStolenFood;
            // A thief may be reused by a hazard slot, so every run starts from the first waypoint.
            Finished = false;
            waypoint = 0;
            var bag = transform.Find("StolenFood");
            if (bag != null) bag.gameObject.SetActive(carryingFood);
            speed = Mathf.Max(0.1f, moveSpeed);
            expiresAt = Time.timeAsDouble + Mathf.Max(1f, lifetime);
            route = new Vector3[points.Length];
            for (int i = 0; i < points.Length; i++) route[i] = points[i].position;
        }
        private void Update()
        {
            if (Finished || session == null) return;
            if (session.IsSettled || (carryingFood && (!session.IsChasingFood || session.Target != transform)) ||
                Time.timeAsDouble >= expiresAt || transform.position.y < -3f ||
                (transform.position - session.Player.position).sqrMagnitude > 3600f) Escape();
        }
        private void FixedUpdate()
        {
            if (Finished || session == null || route == null || session.IsPaused || session.IsSettled) return;
            if (waypoint >= route.Length) { Escape(); return; }
            var direction = Vector3.ProjectOnPlane(route[waypoint] - body.position, Vector3.up);
            if (direction.sqrMagnitude < 0.36f)
            {
                waypoint++;
                if (waypoint >= route.Length) Escape();
                return;
            }
            direction.Normalize();
            body.linearVelocity = new Vector3(direction.x * speed, body.linearVelocity.y, direction.z * speed);
            body.MoveRotation(Quaternion.LookRotation(direction));
        }
        // The thief is caught by a punch, not by running into it.
        public bool ReceivePunch(Vector3 direction)
        {
            if (Finished || session == null) return false;
            if (carryingFood && !session.TryRecoverFood(transform)) return false;
            Finished = true;
            gameObject.SetActive(false);
            return true;
        }
        private void Escape()
        {
            if (Finished) return;
            Finished = true;
            if (session != null) session.RequireReplacement(transform);
            gameObject.SetActive(false);
        }
        private void OnDisable()
        {
            Finished = true;
            if (session != null) session.RequireReplacement(transform);
        }
    }
}
