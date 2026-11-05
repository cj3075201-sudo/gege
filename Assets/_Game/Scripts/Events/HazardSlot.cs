using System;
using UnityEngine;

namespace DeliveryRider
{
    // Objects are always placed for the day and strike any time (even before pickup).
    // Reserved triggers are selected per call and armed throughout delivery, including food recovery.
    public enum HazardRole { Object, Trigger }

    // Planner data for one spawned hazard; HazardPlanner adds it at spawn and turns the hazard on and off.
    [DisallowMultipleComponent]
    public sealed class HazardSlot : MonoBehaviour
    {
        [SerializeField] private string type;
        [SerializeField] private HazardRole role;
        [SerializeField, Min(1)] private int unlockDay = 1;
        [Tooltip("Call routes (shop index x door count + door index) that pass within 7 m.")]
        [SerializeField] private int[] routes = Array.Empty<int>();

        public bool RuntimeActive { get; private set; } = true;
        private bool visualsPaused;
        private Rigidbody[] pausedBodies=Array.Empty<Rigidbody>();
        private bool[] bodyKinematic=Array.Empty<bool>();
        private MonoBehaviour[] controllers;
        private bool[] controllerEnabled;
        private Renderer[] visuals;
        private bool[] visualEnabled;
        private Collider[] volumes;
        private bool[] volumeEnabled;

        // Root objects stay alive for the day. Pausing a slot stops its frame callbacks and hides its visuals.
        // OnEnable is the per-kind reset entry: tire/muscleman/celebrity explicitly restore their spent state.
        public void SetRuntimeActive(bool active, bool reset = false)
        {
            if (controllers == null) RefreshOwnedComponents();
            if(RuntimeActive==active&&!reset&&!visualsPaused)return;
            if(!active||reset)foreach(var controller in controllers)if(controller!=null)controller.enabled=false;
            RestorePausedBodies();
            for(int i=0;i<visuals.Length;i++)if(visuals[i]!=null)visuals[i].enabled=active&&visualEnabled[i];
            for(int i=0;i<volumes.Length;i++)if(volumes[i]!=null)volumes[i].enabled=active&&volumeEnabled[i];
            RuntimeActive=active;visualsPaused=false;
            if(active)for(int i=0;i<controllers.Length;i++)if(controllers[i]!=null)controllers[i].enabled=controllerEnabled[i];
        }

        public void RefreshOwnedComponents()
        {
            var list=new System.Collections.Generic.List<MonoBehaviour>();
            foreach(var component in GetComponentsInChildren<MonoBehaviour>(true))
                if(component!=null&&(component is IPlacedHazard||component.transform!=transform&&component.GetType().Namespace==nameof(DeliveryRider)))list.Add(component);
            var nextControllers=list.ToArray();var nextControllerStates=new bool[nextControllers.Length];
            for(int i=0;i<nextControllers.Length;i++)
            {
                int old=controllers!=null?Array.IndexOf(controllers,nextControllers[i]):-1;
                nextControllerStates[i]=old>=0?controllerEnabled[old]:nextControllers[i].enabled;
            }
            controllers=nextControllers;controllerEnabled=nextControllerStates;
            var nextVisuals=GetComponentsInChildren<Renderer>(true);var nextVisualStates=new bool[nextVisuals.Length];
            for(int i=0;i<nextVisuals.Length;i++){int old=visuals!=null?Array.IndexOf(visuals,nextVisuals[i]):-1;nextVisualStates[i]=old>=0?visualEnabled[old]:nextVisuals[i].enabled;}
            visuals=nextVisuals;visualEnabled=nextVisualStates;
            var nextVolumes=GetComponentsInChildren<Collider>(true);var nextVolumeStates=new bool[nextVolumes.Length];
            for(int i=0;i<nextVolumes.Length;i++){int old=volumes!=null?Array.IndexOf(volumes,nextVolumes[i]):-1;nextVolumeStates[i]=old>=0?volumeEnabled[old]:nextVolumes[i].enabled;}
            volumes=nextVolumes;volumeEnabled=nextVolumeStates;
        }

        public void PauseKeepingVisuals()
        {
            if(!RuntimeActive||visualsPaused)return;
            if(controllers==null)SetRuntimeActive(true);
            var nodes=GetComponentsInChildren<Transform>(true);
            var positions=new Vector3[nodes.Length];var rotations=new Quaternion[nodes.Length];var states=new bool[nodes.Length];
            var visible=new bool[visuals.Length];
            for(int i=0;i<nodes.Length;i++){positions[i]=nodes[i].localPosition;rotations[i]=nodes[i].localRotation;states[i]=nodes[i].gameObject.activeSelf;}
            for(int i=0;i<visuals.Length;i++)visible[i]=visuals[i]!=null&&visuals[i].enabled;
            pausedBodies=GetComponentsInChildren<Rigidbody>(true);bodyKinematic=new bool[pausedBodies.Length];
            for(int i=0;i<pausedBodies.Length;i++){bodyKinematic[i]=pausedBodies[i].isKinematic;pausedBodies[i].isKinematic=true;}
            foreach(var controller in controllers)if(controller!=null)controller.enabled=false;
            if(GetComponent<CloneRiderHazard>()!=null)
            {
                foreach(var volume in volumes)if(volume!=null)volume.enabled=false;
                visualsPaused=true;return;
            }
            for(int i=0;i<nodes.Length;i++)if(nodes[i]!=null){nodes[i].localPosition=positions[i];nodes[i].localRotation=rotations[i];if(nodes[i]!=transform)nodes[i].gameObject.SetActive(states[i]);}
            for(int i=0;i<visuals.Length;i++)if(visuals[i]!=null)visuals[i].enabled=visible[i];
            foreach(var volume in volumes)if(volume!=null)volume.enabled=false;
            visualsPaused=true;
        }

        private void RestorePausedBodies()
        {
            for(int i=0;i<pausedBodies.Length;i++)if(pausedBodies[i]!=null)pausedBodies[i].isKinematic=bodyKinematic[i];
            pausedBodies=Array.Empty<Rigidbody>();bodyKinematic=Array.Empty<bool>();
        }

        public HazardPlanner Planner { get; set; }
        public HazardCandidate Candidate { get; set; }
        public int Combo { get; set; } = -1;
        public bool NeedsCallReset { get; set; }
        public bool Selected { get; set; }
        public bool Consumed { get; set; }
        public string Type => type;
        public HazardRole Role => role;
        public int UnlockDay => unlockDay;
        // Set by the planner; hazards check it before starting an attack.
        public bool StrikeAllowed { get; set; } = true;

        public float TriggerScale { get; private set; } = 1f;
        private bool triggerCaptured;
        private BoxCollider triggerBox;
        private SphereCollider triggerSphere;
        private Vector3 originalBoxSize;
        private float originalSphereRadius;

        // Only these root volumes detect riders. Never resize solids, visuals or a prefab source.
        public void SetTriggerScale(float scale)
        {
            if (!triggerCaptured)
            {
                triggerCaptured = true;
                if (GetComponent<GroundContactHazard>() != null || GetComponent<MusclemanHazard>() != null)
                {
                    triggerBox = GetComponent<BoxCollider>();
                    if (triggerBox != null) originalBoxSize = triggerBox.size;
                }
                if (GetComponent<PigeonFlockHazard>() != null)
                {
                    triggerSphere = GetComponent<SphereCollider>();
                    if (triggerSphere != null) originalSphereRadius = triggerSphere.radius;
                }
            }
            TriggerScale = Mathf.Clamp(scale, 0.5f, 4f);
            if (triggerBox != null) triggerBox.size = originalBoxSize * TriggerScale;
            if (triggerSphere != null) triggerSphere.radius = originalSphereRadius * TriggerScale;
        }

        public bool IsOnRoute(int route) => route >= 0 && Array.IndexOf(routes, route) >= 0;

        public void Configure(string slotType, HazardRole slotRole, int slotUnlockDay, int[] slotRoutes)
        {
            type = slotType;
            role = slotRole;
            unlockDay = slotUnlockDay;
            routes = slotRoutes ?? Array.Empty<int>();
        }
    }
}
