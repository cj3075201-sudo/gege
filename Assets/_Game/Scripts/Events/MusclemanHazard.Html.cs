using UnityEngine;

namespace DeliveryRider
{
    public sealed partial class MusclemanHazard
    {
        [Header("HTML 차도·골목 추격")]
        [SerializeField, Min(.1f)] private float chaseSpeed = 11.5f;
        [SerializeField, Min(.1f)] private float trackingTurnRate = 3f;
        [SerializeField, Min(.1f)] private float directionLockSeconds = .8f;
        [SerializeField, Min(.1f)] private float maximumLockedSeconds = 2.5f;
        [SerializeField, Min(0f)] private float roadsideReach = 3f;
        private RoadNetwork chaseGround;
        private PursuitHazardPath htmlPath;
        private Vector3 htmlOrigin,htmlForward,htmlHeading;
        private bool directionLocked;
        private float lockedElapsed,passElapsed;

        private float HtmlFootHeight => (playerShape!=null?playerShape.bounds.min.y:rider.transform.position.y)-runner.position.y;

        private void ResetHtmlChase()
        {
            if(htmlPath==null||runner==null)return;
            windElapsed=chaseElapsed=lockedElapsed=passElapsed=0f;directionLocked=false;struck.Clear();
            htmlHeading=htmlForward.sqrMagnitude>.01f?htmlForward.normalized:transform.forward;
            runner.position=htmlPath.Ground(htmlOrigin);runner.rotation=Quaternion.LookRotation(htmlHeading);
            runner.gameObject.SetActive(true);
        }
        private void TickHtmlChase()
        {
            if(session.IsMapOpen)return;
            if(slot!=null&&!slot.StrikeAllowed){if(phase!=Phase.Ready)Finish();return;}
            if(phase==Phase.Spent)return;
            float dt=Time.fixedDeltaTime;
            var delta=Vector3.ProjectOnPlane(rider.transform.position-runner.position,Vector3.up);
            float distance=delta.magnitude;
            var forward=Vector3.ProjectOnPlane(rider.HorizontalSpeed>.5f?rider.Velocity:rider.transform.forward,Vector3.up).normalized;
            switch(phase)
            {
                case Phase.Ready:
                    if(Time.time<nextRouteTryAt||distance>detectionRadius*TriggerScale
                        ||Vector3.Dot(delta,forward)<=.3f||HtmlFootHeight>=1.5f)return;
                    slot?.MarkStarted(session);phase=Phase.Winding;windElapsed=0f;htmlHeading=delta.normalized;return;
                case Phase.Winding:
                    windElapsed+=dt;if(delta.sqrMagnitude>.01f)htmlHeading=delta.normalized;
                    runner.rotation=Quaternion.LookRotation(htmlHeading);
                    if(windElapsed>=windSeconds.y||windElapsed>=windSeconds.x&&distance>=spawnBehindDistance)
                    {phase=Phase.Running;chaseElapsed=0f;rushSpeed=chaseSpeed;}
                    return;
                case Phase.Running:
                    chaseElapsed+=dt;
                    if(chaseElapsed>maximumChaseSeconds){StartPass();return;}
                    if(directionLocked){lockedElapsed+=dt;if(lockedElapsed>maximumLockedSeconds){StartPass();return;}}
                    else
                    {
                        htmlHeading=Vector3.RotateTowards(htmlHeading,delta.normalized,trackingTurnRate*dt,0f).normalized;
                        float closing=rushSpeed-Vector3.Dot(Vector3.ProjectOnPlane(rider.Velocity,Vector3.up),htmlHeading);
                        if(distance-runnerRadius<directionLockSeconds*Mathf.Max(1f,closing)){directionLocked=true;lockedElapsed=0f;}
                    }
                    if(!MoveHtml(dt)){StartPass();return;}
                    var after=Vector3.ProjectOnPlane(rider.transform.position-runner.position,Vector3.up);
                    if(after.magnitude<runnerRadius*TriggerScale&&HtmlFootHeight<1.2f)
                    {Shove(playerShape,htmlHeading);StartPass();return;}
                    if(directionLocked&&Vector3.Dot(after,htmlHeading)<-.4f)StartPass();
                    return;
                case Phase.Turning:
                    passElapsed+=dt;
                    MoveHtml(dt);
                    if(passElapsed>=disappearAfterTurn)Finish();
                    return;
            }
        }
        private bool MoveHtml(float dt)
        {
            var from=runner.position;var next=htmlPath.Ground(from+htmlHeading*(rushSpeed*dt));
            if(!htmlPath.CanStep(from,next,.35f))return false;
            runner.SetPositionAndRotation(next,Quaternion.LookRotation(htmlHeading));
            ShoveAlong(from,next,htmlHeading);return true;
        }
        private void StartPass(){phase=Phase.Turning;passElapsed=0f;}
    }
}
