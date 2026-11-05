using UnityEngine;

namespace DeliveryRider
{
    public sealed partial class RiderMotor
    {
        private bool hazardStunned;
        private float hazardStunUntil;

        // A grounded fall uses the existing prone/get-up visuals, without entering flight or adding an impulse.
        public bool TryHazardStun(UnityEngine.Object source, float seconds)
        {
            if (!AcceptHazard(source)) return false;
            slipOwner = null;
            physicalTumble = false;
            jumpQueued = normalJumpActive = false;
            movement = airMovement = Vector2.zero;
            pendingStrength = 0f;
            hazardStunned = true;
            hazardStunUntil = Time.time + Mathf.Max(.1f, seconds);
            nextTrafficHitAt = hazardStunUntil + 1f;
            body.linearVelocity = Vector3.up * Mathf.Min(0f, body.linearVelocity.y);
            body.angularVelocity = Vector3.zero;
            SetReactionState(RiderReactionState.Rolling);
            HazardHit?.Invoke(source);
            return true;
        }

        private bool TickHazardStun()
        {
            if (!hazardStunned) return false;
            IsReactionGrounded = TryReactionSupport();
            groundedForHazards = IsReactionGrounded;
            body.linearVelocity = IsReactionGrounded ? Vector3.zero : Vector3.up * Mathf.Min(0f, body.linearVelocity.y);
            if (Time.time >= hazardStunUntil)
            {
                hazardStunned = false;
                SetReactionState(RiderReactionState.Normal);
            }
            else if (IsReactionGrounded && Time.time >= hazardStunUntil - getUpSeconds)
                SetReactionState(RiderReactionState.GettingUp);
            collisionVelocity = body.linearVelocity;
            return true;
        }
    }
}
