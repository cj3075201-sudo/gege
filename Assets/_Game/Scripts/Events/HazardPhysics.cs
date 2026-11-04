using UnityEngine;

namespace DeliveryRider
{
    // Zone barriers stop only the rider, so "anything in the way" checks for hazards and walkers must not see them.
    public static class HazardPhysics
    {
        public const string BarrierLayerName = "ZoneBarrier";
        private static int everything;
        private static bool resolved;

        public static int Everything
        {
            get
            {
                if (resolved) return everything;
                int layer = LayerMask.NameToLayer(BarrierLayerName);
                everything = layer >= 0 ? ~(1 << layer) : ~0;
                resolved = true;
                return everything;
            }
        }
    }
}
