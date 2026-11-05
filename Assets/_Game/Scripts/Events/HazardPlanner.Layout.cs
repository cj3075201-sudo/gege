using System.Collections.Generic;
using UnityEngine;

namespace DeliveryRider
{
    public sealed partial class HazardPlanner
    {
        private readonly List<(HazardSpawnSettings.Kind kind, HazardCandidate candidate, int combo)> layoutPlan = new List<(HazardSpawnSettings.Kind, HazardCandidate, int)>();
        private readonly Dictionary<string, List<HazardCandidate>> candidatesByType = new Dictionary<string, List<HazardCandidate>>();
        private readonly List<HazardCandidate> availableCandidates = new List<HazardCandidate>();
        private readonly Dictionary<string, int> layoutRemaining = new Dictionary<string, int>();
        private readonly List<HazardCandidate> layoutPlaced = new List<HazardCandidate>();
        private readonly List<Vector3> comboCenters = new List<Vector3>();
        private readonly List<int> layoutIndexes = new List<int>();
        private static string LayoutKey(string type, bool alley) => type + (alley ? ":alley" : ":main");

        private void PlaceDailyLayout(System.Random random, Transform staging, int attempt = 0)
        {
            availableCandidates.Clear(); candidatesByType.Clear();
            foreach (var raw in candidates.Candidates)
            {
                var c = raw;
                c.alley = c.halfWidth > 0f ? c.halfWidth < 3f : c.alley;
                if (c.type == "muscleman" || c.type == "celebrity")
                {
                    float nearest = float.PositiveInfinity;
                    foreach (var road in ground.Roads)
                    {
                        float distance = road.DistanceToCentre(c.position) - road.HalfWidth;
                        if (distance < nearest) { nearest = distance; c.alley = road.HalfWidth < 3f; }
                    }
                }
                if (GroundObject(c.type) && c.spot >= 0 && c.spot < candidates.Spots.Count)
                {
                    var center = candidates.Spots[c.spot].position;
                    c.position = center + (c.type == "manhole" ? c.rotation * Vector3.right * c.side * c.halfWidth * .3f
                        : -(c.rotation * Vector3.forward) * Mathf.Max(.4f, c.halfWidth - 1.2f));
                    c.position = ground.OnGround(c.position, c.type == "manhole" ? .008f : 0f);
                }
                if (CandidateOpen(c))
                {
                    availableCandidates.Add(c);
                    string key = LayoutKey(c.type,c.alley);
                    if (!candidatesByType.TryGetValue(key,out var list)) candidatesByType[key] = list = new List<HazardCandidate>();
                    list.Add(c);
                }
            }
            layoutPlan.Clear();
            layoutRemaining.Clear(); layoutPlaced.Clear(); comboCenters.Clear(); PlacedCombos = 0;
            float mainOpen = 0f, mainTotal = 0f, alleyOpen = 0f, alleyTotal = 0f;
            foreach (var road in ground.Roads)
            {
                var spline = road.Container;
                if (road.HalfWidth < 3f) openRoads.AddRoad(ref alleyOpen, ref alleyTotal, spline.CalculateLength(), t => (Vector3)spline.EvaluatePosition(t));
                else openRoads.AddRoad(ref mainOpen, ref mainTotal, spline.CalculateLength(), t => (Vector3)spline.EvaluatePosition(t));
            }
            float mainShare = mainTotal > 0f ? mainOpen / mainTotal : 1f, alleyShare = alleyTotal > 0f ? alleyOpen / alleyTotal : 1f;
            foreach (var kind in settings.Kinds)
            {
                if (kind.prefab == null || kind.unlockDay > day) continue;
                int total = kind.Count(day);
                int alley = kind.role == HazardRole.Trigger && kind.mainPoolSize >= 0 ? kind.alleyPoolSize : Mathf.RoundToInt(total * kind.objectAlleyShare);
                int main = kind.role == HazardRole.Trigger && kind.mainPoolSize >= 0 ? kind.mainPoolSize : total - alley;
                main = HazardZoneFilter.ScaleCount(HazardZoneFilter.ScaleCount(main, mainShare), hazardCountScale);
                alley = HazardZoneFilter.ScaleCount(HazardZoneFilter.ScaleCount(alley, alleyShare), hazardCountScale);
                layoutRemaining[LayoutKey(kind.type, false)] = main;
                layoutRemaining[LayoutKey(kind.type, true)] = alley;
                requestedCounts[kind.type] = main + alley;
            }
            if (combosEnabled)
                foreach (var template in settings.Combos)
                {
                    int want = Mathf.RoundToInt(template.count * comboCountScale * (template.alley ? alleyShare : mainShare));
                    for (int n = 0; n < want; n++) TryPlaceCombo(template, random, staging);
                }
            // Random placement keeps each type distributed across the open map.
            for (int priority = 0; priority < 6; priority++)
                foreach (var kind in settings.Kinds)
                {
                    if (kind.prefab == null || kind.unlockDay > day || (kind.type == "celebrity" ? 0 : kind.type == "falling" ? 1 : kind.type == "muscleman" ? 2 : kind.type == "pigeonflock" ? 3 : kind.role == HazardRole.Object ? 4 : 5) != priority) continue;
                    foreach (bool alley in new[] { false, true })
                    {
                        string key = LayoutKey(kind.type, alley);
                        layoutIndexes.Clear();
                        for (int i = 0; i < availableCandidates.Count; i++)
                        {
                            var c = availableCandidates[i];
                            if (c.type == kind.type && IsAlley(c) == alley) layoutIndexes.Add(i);
                        }
                        Shuffle(layoutIndexes, random);
                        foreach (int index in layoutIndexes)
                        {
                            if (layoutRemaining[key] <= 0) break;
                            var c = availableCandidates[index];
                            if (!LayoutRoom(c, false)) continue;
                            layoutPlan.Add((kind,c,-1)); layoutPlaced.Add(c); layoutRemaining[key]--;
                        }
                        if (attempt >= 5 && layoutRemaining[key] > 0) Debug.LogWarning($"Hazards: missing {layoutRemaining[key]} {key} at requested spacing.", this);
                    }
                }
            int missing = 0; foreach (var left in layoutRemaining.Values) missing += left;
            if (missing > 0 && attempt < 5) { PlaceDailyLayout(random, staging, attempt + 1); return; }
            // The HTML's ten templates contain no clone member. Keep its safe baked position and attach its draw
            // to the nearest existing combo within the same 40 m chain radius, without adding/moving a slot.
            for(int i=0;i<layoutPlan.Count;i++)
            {
                var item=layoutPlan[i]; if(item.kind.type!="clone"||item.combo>=0)continue;
                int combo=-1;float best=igniteRadius*igniteRadius;
                foreach(var other in layoutPlan)
                {
                    if(other.combo<0||other.candidate.alley!=item.candidate.alley)continue;
                    float distance=Vector3.ProjectOnPlane(other.candidate.position-item.candidate.position,Vector3.up).sqrMagnitude;
                    if(distance<best){best=distance;combo=other.combo;}
                }
                if(combo>=0)layoutPlan[i]=(item.kind,item.candidate,combo);
            }
            foreach (var item in layoutPlan) { var slot = SpawnOne(item.kind, item.candidate, staging); slot.Combo = item.combo; }
        }

        private bool IsAlley(HazardCandidate c) => c.alley;
        private static bool GroundObject(string type) => type == "manhole" || type == "hydrant";
        private bool LayoutRoom(HazardCandidate c, bool withinCombo, List<HazardCandidate> extra = null)
        {
            bool Clear(HazardCandidate other, bool sameCombo)
            {
                float gap = GroundObject(c.type) && GroundObject(other.type) ? Mathf.Max(objectMinimumSpacing, (c.type == other.type ? settings.SameTypeSpacing : settings.Spacing) > 9f ? (c.type == other.type ? settings.SameTypeSpacing : settings.Spacing) : 0f)
                    : sameCombo ? 2.9f
                    : settings.Combos.Count > 0 && !GroundObject(c.type) && !GroundObject(other.type) && c.type != other.type ? 2.9f
                    : Mathf.Max(9f, c.type == other.type ? settings.SameTypeSpacing : settings.Spacing);
                return Vector3.ProjectOnPlane(c.position - other.position, Vector3.up).sqrMagnitude >= gap * gap;
            }
            foreach (var other in layoutPlaced) if (!Clear(other, false)) return false;
            if (extra != null) foreach (var other in extra) if (!Clear(other, withinCombo)) return false;
            return true;
        }

        private void TryPlaceCombo(HazardSpawnSettings.ComboTemplate template, System.Random random, Transform staging)
        {
            var assembled = new List<HazardCandidate>();
            var kinds = new List<HazardSpawnSettings.Kind>();
            for (int attempt = 0; attempt < 250; attempt++)
            {
                assembled.Clear(); kinds.Clear();
                var anchor = availableCandidates[random.Next(availableCandidates.Count)];
                if (IsAlley(anchor) != template.alley) continue;
                Vector3 center = anchor.spot >= 0 && anchor.spot < candidates.Spots.Count ? candidates.Spots[anchor.spot].position : anchor.position;
                if (comboCenters.Exists(p => (p - center).sqrMagnitude < (template.alley ? 400f : 900f))) continue;
                var direction = anchor.rotation * Vector3.forward;
                if (anchor.type == "hydrant" || anchor.type == "muscleman" || anchor.type == "celebrity") continue;
                var right = Vector3.Cross(Vector3.up, direction); int side = random.Next(2) == 0 ? -1 : 1;
                foreach (var member in template.members)
                {
                    string type = member.type == "B" ? (template.alley ? "manhole" : "hydrant") : member.type;
                    HazardSpawnSettings.Kind kind = null;
                    foreach (var k in settings.Kinds) if (k.type == type && k.unlockDay <= day && k.prefab != null) { kind = k; break; }
                    if (kind == null) continue; // Locked members do not exist yet, matching HTML's day filter.
                    int used = 0; foreach (var c in assembled) if (c.type == type) used++;
                    if (!layoutRemaining.TryGetValue(LayoutKey(type, template.alley), out int left) || left <= used) { assembled.Clear(); break; }
                    float lat = member.lateralFraction >= 0f ? anchor.halfWidth * member.lateralFraction : Mathf.Max(.4f, anchor.halfWidth - 1.2f);
                    var target = center + direction * member.along + right * (side * member.side * lat);
                    int best = -1; float distance = 36f;
                    if (!candidatesByType.TryGetValue(LayoutKey(type,template.alley),out var choices)) { assembled.Clear(); break; }
                    HazardCandidate chosen = default;
                    for (int i = 0; i < choices.Count; i++)
                    {
                        var c = choices[i];
                        float d = Vector3.ProjectOnPlane(c.position - target, Vector3.up).sqrMagnitude;
                        if (d >= distance || !LayoutRoom(c, true, assembled)) continue;
                        // Keep specialist sidewalk/escape candidates at their baked safe locations.
                        best = i; chosen = c; distance = d;
                    }
                    if (best < 0)
                    {
                        if (member.type == "B") continue; // The 5 m art gap takes priority over a replacement object.
                        assembled.Clear(); break;
                    }
                    assembled.Add(chosen); kinds.Add(kind);
                }
                if (assembled.Count < 2) continue;
                int combo = PlacedCombos++;
                for (int i = 0; i < assembled.Count; i++)
                {
                    var c = assembled[i]; layoutPlan.Add((kinds[i], c, combo));
                    layoutPlaced.Add(c); layoutRemaining[LayoutKey(c.type, template.alley)]--;
                }
                comboCenters.Add(center); return;
            }
        }
        private static void Shuffle(List<int> list, System.Random random)
        {
            for (int i = list.Count - 1; i > 0; i--) { int j = random.Next(i + 1); (list[i], list[j]) = (list[j], list[i]); }
        }
    }
}
