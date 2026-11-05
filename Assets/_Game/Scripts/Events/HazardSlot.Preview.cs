using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeliveryRider
{
    public sealed partial class HazardSlot
    {
        private const uint PreviewFlag=0x40000000u,PreviewDataMask=0x40ff0000u;
        private uint[] originalLayers=Array.Empty<uint>();
        private ShadowCastingMode[] originalShadows=Array.Empty<ShadowCastingMode>();
        private bool previewPreparing,ghostFading,lastGhostFlag;
        private float fadeStarted,fadeDuration,fadeFrom;
        public bool IsPreviewVisible { get; private set; }
        public float GhostOpacity { get; private set; }=1f;

        private void CacheGhostRenderers(Renderer[] next)
        {
            var layers=new uint[next.Length];var shadows=new ShadowCastingMode[next.Length];
            for(int i=0;i<next.Length;i++)
            {
                int old=visuals!=null?Array.IndexOf(visuals,next[i]):-1;
                if(old>=0){layers[i]=originalLayers[old];shadows[i]=originalShadows[old];}
                else
                {
                    uint current=next[i].renderingLayerMask;layers[i]=current&~PreviewDataMask;
                    shadows[i]=(current&PreviewFlag)!=0?ShadowCastingMode.On:next[i].shadowCastingMode;
                }
            }
            originalLayers=layers;originalShadows=shadows;
            if(IsPreviewVisible||ghostFading)ApplyGhostOpacity(GhostOpacity,next);
        }
        public void SetPreview(float opacity)
        {
            if(Role!=HazardRole.Trigger)return;
            if(IsPreviewVisible){ApplyGhostOpacity(opacity);return;}
            StrikeAllowed=false;previewPreparing=true;
            try
            {
                SetRuntimeActive(true,true);
                if(TryGetComponent<DogHazard>(out var dog))dog.ShowWaitingPreview();
                if(TryGetComponent<CatBoxerHazard>(out var cat))cat.ShowWaitingPreview();
                if(TryGetComponent<CloneRiderHazard>(out var clone))clone.ShowWaitingPreview();
                if(TryGetComponent<CelebrityCrowdHazard>(out var crowd))crowd.ShowWaitingPreview();
                PauseKeepingVisuals();RuntimeActive=false;IsPreviewVisible=true;
                ApplyGhostOpacity(opacity);
            }
            finally{previewPreparing=false;StrikeAllowed=false;}
        }
        public void ActivateFromPreview(float seconds)
        {
            float from=GhostOpacity;
            SetRuntimeActive(true,true);
            if(TryGetComponent<CelebrityCrowdHazard>(out var crowd))crowd.ShowWaitingPreview();
            ghostFading=seconds>0f;fadeFrom=from;fadeDuration=seconds;fadeStarted=Time.time;
            ApplyGhostOpacity(ghostFading?from:1f);
        }
        public void TickGhostFade()
        {
            if(!ghostFading)return;
            float t=fadeDuration>0f?Mathf.Clamp01((Time.time-fadeStarted)/fadeDuration):1f;
            ApplyGhostOpacity(Mathf.Lerp(fadeFrom,1f,t));
            if(t>=1f)ghostFading=false;
        }
        private void ApplyGhostOpacity(float opacity,Renderer[] targets=null)
        {
            opacity=Mathf.Clamp01(opacity);
            bool ghost=IsPreviewVisible||ghostFading&&opacity<.999f;
            if(targets==null&&Mathf.Approximately(opacity,GhostOpacity)&&lastGhostFlag==ghost)return;
            lastGhostFlag=ghost;
            GhostOpacity=opacity;targets??=visuals;if(targets==null)return;
            uint encoded=PreviewFlag|((uint)Mathf.RoundToInt(opacity*255f)<<16);
            for(int i=0;i<targets.Length;i++)
            {
                if(targets[i]==null)continue;
                targets[i].renderingLayerMask=ghost?originalLayers[i]|encoded:originalLayers[i];
                targets[i].shadowCastingMode=ghost?ShadowCastingMode.Off:originalShadows[i];
            }
        }
    }
}
