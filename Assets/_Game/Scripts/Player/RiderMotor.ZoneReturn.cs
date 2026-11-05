using UnityEngine;

namespace DeliveryRider
{
    // A rider who ends up in a closed zone hops back to the nearest open road: after a tumble once they stop, otherwise
    // whenever they are found standing there (a jump round a wall end, landing on a wall, pushed by a car). Crossing in
    // the air is fine; only standing counts.
    // The arc is fixed and passes through walls, like a recovery; food, order and timers are left alone.
    public sealed partial class RiderMotor
    {
        [Header("Return from a closed zone (test values)")]
        [SerializeField, Min(0.1f)] private float zoneHopSpeed = 13f;
        [Tooltip("Shortest and longest hop.")]
        [SerializeField] private Vector2 zoneHopSeconds = new Vector2(0.55f, 1.5f);
        [SerializeField, Min(0f)] private float zoneHopHeight = 1.1f;
        [SerializeField, Min(0f)] private float zoneHopHeightPerMeter = 0.054f;
        [Tooltip("Share of the hop's horizontal speed kept on landing.")]
        [SerializeField, Range(0f, 1f)] private float zoneHopKeptSpeed = 0.3f;
        [Tooltip("How often a rider standing normally is checked for being in a closed zone. A failed search is retried at the next check.")]
        [SerializeField, Min(0.02f)] private float zoneCheckSeconds = 0.25f;
        private float nextZoneCheckAt;
        private HazardZoneFilter openRoads;
        private bool zoneHopping;
        private Vector3 zoneHopFrom, zoneHopTo;
        private float zoneHopElapsed, zoneHopDuration, zoneHopArc;
        private CollisionDetectionMode zoneHopCollisionMode;

        // Null without a zone map, which leaves every recovery exactly as it was.
        private HazardZoneFilter OpenRoads
        {
            get
            {
                if (session == null || session.ZoneMap == null) return null;
                return openRoads ?? (openRoads = new HazardZoneFilter(session.ZoneMap, session.IsZoneOpen));
            }
        }

        public static float ZoneHopDuration(float distance, float speed, float shortest, float longest)
            => Mathf.Clamp(distance / Mathf.Max(0.01f, speed), shortest, Mathf.Max(shortest, longest));

        public static Vector3 ZoneHopPoint(Vector3 from, Vector3 to, float progress, float arc)
        {
            float k = Mathf.Clamp01(progress);
            return Vector3.Lerp(from, to, k) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * arc);
        }

        private bool TryReturnFromClosedZone()
        {
            if (Time.time < nextZoneCheckAt) return false;
            nextZoneCheckAt = Time.time + zoneCheckSeconds;
            return groundedForHazards && !normalJumpActive && !session.IsSettled && TryBeginZoneHop();
        }

        // A closed-zone position must never become the place to recover to.
        private bool SpawnAllowedHere => (safeRecovery == null || safeRecovery.IsSafe(body.position))
            && (OpenRoads == null || OpenRoads.PointOpen(body.position));

        private bool TryBeginZoneHop()
        {
            var roads = OpenRoads;
            if (roads == null || safeRecovery == null || !safeRecovery.LandedClosed(body.position, roads)
                || !safeRecovery.TryFindOpenRoad(body.position, roads, out var destination)) return false;
            float distance = Vector3.ProjectOnPlane(destination - body.position, Vector3.up).magnitude;
            zoneHopFrom = body.position;
            zoneHopTo = destination;
            zoneHopElapsed = 0f;
            zoneHopDuration = ZoneHopDuration(distance, zoneHopSpeed, zoneHopSeconds.x, zoneHopSeconds.y);
            zoneHopArc = zoneHopHeight + distance * zoneHopHeightPerMeter;
            pendingStrength = 0f;
            body.linearVelocity = body.angularVelocity = Vector3.zero;
            // A kinematic body supports only speculative collision detection.
            zoneHopCollisionMode = body.collisionDetectionMode;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.isKinematic = true;
            zoneHopping = true;
            SetReactionState(RiderReactionState.Airborne);
            return true;
        }

        private void AdvanceZoneHop()
        {
            zoneHopElapsed += Time.fixedDeltaTime;
            if (zoneHopElapsed < zoneHopDuration)
            {
                body.MovePosition(ZoneHopPoint(zoneHopFrom, zoneHopTo, zoneHopElapsed / zoneHopDuration, zoneHopArc));
                return;
            }
            CancelZoneHop();
            body.position = zoneHopTo;
            var kept = Vector3.ProjectOnPlane(zoneHopTo - zoneHopFrom, Vector3.up) / zoneHopDuration * zoneHopKeptSpeed;
            body.linearVelocity = kept;
            collisionVelocity = kept;
            spawn = zoneHopTo;
            nextTrafficHitAt = Mathf.Max(nextTrafficHitAt, Time.time + 1f);
            SetReactionState(RiderReactionState.Normal);
        }

        private void CancelZoneHop()
        {
            if (!zoneHopping) return;
            zoneHopping = false;
            body.isKinematic = false;
            body.collisionDetectionMode = zoneHopCollisionMode;
        }
    }
}
