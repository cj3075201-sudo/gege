using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// Generates assets only. The production scene, map prefab and source mesh stay untouched.
public static class MyeongjiGroundGenerator
{
    private const string Geometry = "Assets/_Game/Art/Myeongji/Geometry/";
    private const string SourcePath = Geometry + "Ground_SidewalkStage15.asset";
    private const string GroundPath = Geometry + "Ground_Expanded.asset";
    private const string MarkingsPath = Geometry + "Ground_ExpandedMarkings.asset";
    private const string Input = "Docs/MapLayout/Myeongji/";
    private const string Results = "../공유/결과/";
    private const float RoadY = -0.14f;
    private const float SidewalkWidth = 2f;
    private const float Epsilon = 0.00001f;
    private const double Weld = 0.0001; // 0.1 mm; also joins the imported FBX float noise.
    private static readonly string[] SlotNames = { "Plot", "Pavement", "Asphalt", "Curb" };

    public static void VerifySaved()
    {
        Directory.CreateDirectory(Results);
        var source = AssetDatabase.LoadAssetAtPath<Mesh>(SourcePath);
        if (source == null) throw new Exception("Missing source ground");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Game/Prefabs/Myeongji/MyeongjiMap.prefab");
        var filter = prefab.transform.Find("Ground").GetComponentInChildren<MeshFilter>(true);
        var matrix = filter.transform.localToWorldMatrix;
        var verts = source.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
        var slots = filter.GetComponent<Renderer>().sharedMaterials.Select(m => m.name).ToArray();
        var summary = new
        {
            matrix = matrix.ToString(), slots,
            bounds = new { min = new[] { verts.Min(v => v.x), verts.Min(v => v.y), verts.Min(v => v.z) },
                max = new[] { verts.Max(v => v.x), verts.Max(v => v.y), verts.Max(v => v.z) } },
            submeshes = Enumerable.Range(0, source.subMeshCount).Select(s => new
            {
                slot = s, triangles = source.GetTriangles(s).Length / 3,
                heights = source.GetTriangles(s).Select(i => Math.Round(verts[i].y, 5)).Distinct().OrderBy(y => y).ToArray(),
                projectedArea = ProjectedArea(verts, source.GetTriangles(s)),
            }).ToArray(),
        };
        File.WriteAllText(Results + "codex_ground_baseline.json", JsonConvert.SerializeObject(summary, Formatting.Indented));
        Debug.Log("GROUND_BASELINE " + JsonConvert.SerializeObject(summary));
        var ground = AssetDatabase.LoadAssetAtPath<Mesh>(GroundPath);
        if (ground == null) throw new Exception("GROUND_VERIFY FAIL: Ground_Expanded asset has not been generated");
        if (ground.subMeshCount != 4) throw new Exception("Expected Plot/Pavement/Asphalt/Curb slots");
        Verify(ground, matrix, null);
    }

    private static double ProjectedArea(Vector3[] vertices, int[] ids)
    {
        double area = 0;
        for (int i = 0; i < ids.Length; i += 3)
        {
            var normal = Vector3.Cross(vertices[ids[i + 1]] - vertices[ids[i]], vertices[ids[i + 2]] - vertices[ids[i]]);
            if (normal.y > 0) area += normal.y * 0.5;
        }
        return area;
    }

    private sealed class Road
    {
        public string Id;
        public Vector3 A, B;
        public float Width;
        public Vector3 Direction => (B - A).normalized;
        public Vector3 Normal => new Vector3(-Direction.z, 0, Direction.x);
        public float Length => Vector3.Distance(A, B);
    }

    private sealed class Face
    {
        public List<Vector3> Points;
        public int Slot;
        public Rect Bounds;
        public Face(List<Vector3> points, int slot)
        {
            Points = points;
            Slot = slot;
            Bounds = BoundsOf(points);
        }
    }

    private readonly struct PointKey : IEquatable<PointKey>
    {
        public readonly long X, Z;
        public PointKey(Vector3 p) { X = (long)Math.Round(p.x / Weld); Z = (long)Math.Round(p.z / Weld); }
        public bool Equals(PointKey b) => X == b.X && Z == b.Z;
        public override bool Equals(object o) => o is PointKey b && Equals(b);
        public override int GetHashCode() => unchecked((X.GetHashCode() * 397) ^ Z.GetHashCode());
        public Vector3 Position => new Vector3((float)(X * Weld), 0, (float)(Z * Weld));
        public int CompareTo(PointKey b) => X != b.X ? X.CompareTo(b.X) : Z.CompareTo(b.Z);
    }

    private readonly struct EdgeKey : IEquatable<EdgeKey>
    {
        public readonly PointKey A, B;
        public EdgeKey(Vector3 a, Vector3 b)
        {
            var x = new PointKey(a); var z = new PointKey(b);
            A = x.CompareTo(z) <= 0 ? x : z; B = x.CompareTo(z) <= 0 ? z : x;
        }
        public bool Equals(EdgeKey b) => A.Equals(b.A) && B.Equals(b.B);
        public override bool Equals(object o) => o is EdgeKey b && Equals(b);
        public override int GetHashCode() => unchecked(A.GetHashCode() * 397 ^ B.GetHashCode());
    }

    private sealed class EdgeSide
    {
        public Vector3 A, B;
        public int Slot;
    }

    private sealed class MeshBuilder
    {
        public readonly List<Vector3> Vertices = new List<Vector3>();
        public readonly List<int>[] Triangles;
        private readonly Dictionary<(long, long, long, int), int> lookup = new Dictionary<(long, long, long, int), int>();
        public MeshBuilder(int slots) { Triangles = Enumerable.Range(0, slots).Select(_ => new List<int>()).ToArray(); }
        private int Vertex(Vector3 p, int slot)
        {
            var key = ((long)Math.Round(p.x / Weld), (long)Math.Round(p.y / Weld), (long)Math.Round(p.z / Weld), slot);
            if (lookup.TryGetValue(key, out int id)) return id;
            id = Vertices.Count; lookup.Add(key, id);
            Vertices.Add(new Vector3((float)(key.Item1 * Weld), (float)(key.Item2 * Weld), (float)(key.Item3 * Weld)));
            return id;
        }
        public void Triangle(Vector3 a, Vector3 b, Vector3 c, int slot)
        {
            if (Vector3.Cross(b - a, c - a).sqrMagnitude < 1e-17f) return;
            int x = Vertex(a, slot), y = Vertex(b, slot), z = Vertex(c, slot);
            if (x == y || y == z || z == x) return;
            Triangles[slot].Add(x); Triangles[slot].Add(y); Triangles[slot].Add(z);
        }
        public void Polygon(List<Vector3> points, int slot)
        {
            for (int i = 1; i < points.Count - 1; i++) Triangle(points[0], points[i], points[i + 1], slot);
        }
        public Mesh Build(string name, Matrix4x4 toLocal)
        {
            var m = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            m.SetVertices(Vertices.Select(toLocal.MultiplyPoint3x4).ToList());
            m.SetUVs(0, Vertices.Select(p => new Vector2(p.x * .25f, p.z * .25f)).ToList());
            m.subMeshCount = Triangles.Length;
            for (int i = 0; i < Triangles.Length; i++) m.SetTriangles(Triangles[i], i);
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }
    }

    private static Road[] ReadRoads()
    {
        return JObject.Parse(File.ReadAllText(Input + "layout_expanded.json"))["roads"].Select(r => new Road
        {
            Id = (string)r["id"], A = Xz(r["a"]), B = Xz(r["b"]), Width = (float)r["width"],
        }).ToArray();
    }

    private static Vector3 Xz(JToken p) => new Vector3((float)p[0], 0, (float)p[1]);

    private static Rect ReadBounds()
    {
        var bounds = JObject.Parse(File.ReadAllText(Input + "zones.json"))["bounds"];
        return new Rect((float)bounds["minimum"][0], (float)bounds["minimum"][1],
            (float)bounds["size"][0], (float)bounds["size"][1]);
    }

    private static Rect BoundsOf(List<Vector3> points)
    {
        return Rect.MinMaxRect(points.Min(p => p.x), points.Min(p => p.z), points.Max(p => p.x), points.Max(p => p.z));
    }

    private static bool Overlaps(Rect a, Rect b) => a.xMin <= b.xMax + Epsilon && a.xMax >= b.xMin - Epsilon &&
        a.yMin <= b.yMax + Epsilon && a.yMax >= b.yMin - Epsilon;

    private static List<Vector3> Rectangle(Rect r, float y = 0) => new List<Vector3>
    {
        new Vector3(r.xMin, y, r.yMin), new Vector3(r.xMin, y, r.yMax),
        new Vector3(r.xMax, y, r.yMax), new Vector3(r.xMax, y, r.yMin),
    };

    // Clockwise in XZ, therefore upward in Unity. Square caps close dead ends and angled joints.
    private static List<Vector3> Corridor(Road road, float extra)
    {
        float h = road.Width * .5f + extra;
        var a = road.A - road.Direction * h;
        var b = road.B + road.Direction * h;
        return new List<Vector3> { a - road.Normal * h, a + road.Normal * h, b + road.Normal * h, b - road.Normal * h };
    }

    // Stage01's Split half-plane operation, with a double distance calculation for long diagonal roads.
    private static void Split(List<Vector3> polygon, Vector3 a, Vector3 b, out List<Vector3> inside, out List<Vector3> outside)
    {
        inside = new List<Vector3>(); outside = new List<Vector3>();
        if (polygon.Count == 0) return;
        double D(Vector3 p) => (double)(b.z - a.z) * (p.x - a.x) - (double)(b.x - a.x) * (p.z - a.z);
        var previous = polygon[polygon.Count - 1]; double before = D(previous);
        foreach (var current in polygon)
        {
            double after = D(current);
            if ((before >= 0) != (after >= 0))
            {
                var hit = previous + (current - previous) * (float)(before / (before - after));
                inside.Add(hit); outside.Add(hit);
            }
            if (after >= 0) inside.Add(current); else outside.Add(current);
            previous = current; before = after;
        }
    }

    private static double Area(List<Vector3> p)
    {
        double sum = 0;
        for (int i = 1; i < p.Count - 1; i++)
            sum += (double)(p[i].z - p[0].z) * (p[i + 1].x - p[0].x) - (double)(p[i].x - p[0].x) * (p[i + 1].z - p[0].z);
        return sum * .5;
    }

    private static void AddFace(List<Face> faces, List<Vector3> polygon, int slot)
    {
        if (polygon.Count >= 3 && Area(polygon) > 1e-9) faces.Add(new Face(polygon, slot));
    }

    private static List<Vector3> Intersect(List<Vector3> p, List<Vector3> clip)
    {
        for (int i = 0; i < clip.Count && p.Count >= 3; i++)
        {
            Split(p, clip[i], clip[(i + 1) % clip.Count], out var inside, out _); p = inside;
        }
        return p;
    }

    private static List<Face> Stamp(List<Face> faces, List<Vector3> clip, int slot, float y)
    {
        var bounds = BoundsOf(clip);
        var result = new List<Face>(faces.Count + 128);
        foreach (var face in faces)
        {
            // Existing asphalt and accessibility ramps remain intact when adding a sidewalk.
            if (!Overlaps(face.Bounds, bounds) || (slot == 1 && face.Slot != 0)) { result.Add(face); continue; }
            var inside = Intersect(face.Points, clip);
            if (inside.Count < 3 || Area(inside) < 1e-9) { result.Add(face); continue; }
            var remaining = face.Points;
            for (int i = 0; i < clip.Count && remaining.Count >= 3; i++)
            {
                Split(remaining, clip[i], clip[(i + 1) % clip.Count], out var next, out var outside);
                AddFace(result, outside, face.Slot); remaining = next;
            }
            AddFace(result, inside.Select(p => new Vector3(p.x, y, p.z)).ToList(), slot);
        }
        return result;
    }

    private static Matrix4x4 SourceMatrix(out Material[] materials)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Game/Prefabs/Myeongji/MyeongjiMap.prefab");
        var filter = prefab.transform.Find("Ground").GetComponentInChildren<MeshFilter>(true);
        materials = filter.GetComponent<Renderer>().sharedMaterials;
        if (materials.Length != 4 || materials.Where((m, i) => !m.name.Contains(SlotNames[i])).Any())
            throw new Exception("Original material slot contract changed");
        return filter.transform.localToWorldMatrix;
    }

    [MenuItem("Tools/Delivery Rider/Expanded Map/Generate Ground Assets")]
    public static void Generate()
    {
        if (EditorApplication.isPlaying) throw new Exception("Ground generation requires edit mode");
        Directory.CreateDirectory(Results);
        var markingFilter = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Game/Prefabs/Myeongji/MyeongjiMap.prefab")
            .transform.Find("Markings").GetComponentInChildren<MeshFilter>(true);
        var markingVertices = markingFilter.sharedMesh.vertices;
        var markingIds = markingFilter.sharedMesh.triangles;
        Debug.Log("GROUND_MARKINGS_SOURCE " + JsonConvert.SerializeObject(new
        {
            slots = markingFilter.GetComponent<Renderer>().sharedMaterials.Select(m => m.name).ToArray(),
            matrix = markingFilter.transform.localToWorldMatrix.ToString(),
            edgeLengths = markingIds.Select((id, i) => i % 3 == 0 ?
                Vector3.Distance(markingVertices[id], markingVertices[markingIds[i + 1]]) : 0)
                .Where(d => d > .001f).Select(d => Math.Round(d, 2)).Distinct().OrderBy(d => d).Take(45).ToArray(),
        }));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var source = AssetDatabase.LoadAssetAtPath<Mesh>(SourcePath);
        var matrix = SourceMatrix(out var materials);
        var vertices = source.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
        var faces = new List<Face>();
        // Keep every playable source triangle, including existing ramps; underside/skirt faces are not walkable surfaces.
        for (int sub = 0; sub < source.subMeshCount; sub++)
        {
            var ids = source.GetTriangles(sub);
            for (int i = 0; i < ids.Length; i += 3)
                AddFace(faces, new List<Vector3> { vertices[ids[i]], vertices[ids[i + 1]], vertices[ids[i + 2]] }, sub);
        }
        var old = Rect.MinMaxRect(vertices.Min(v => v.x), vertices.Min(v => v.z), vertices.Max(v => v.x), vertices.Max(v => v.z));
        var bounds = ReadBounds();
        AddFace(faces, Rectangle(Rect.MinMaxRect(bounds.xMin, bounds.yMin, old.xMin, bounds.yMax)), 0);
        AddFace(faces, Rectangle(Rect.MinMaxRect(old.xMax, bounds.yMin, bounds.xMax, bounds.yMax)), 0);
        AddFace(faces, Rectangle(Rect.MinMaxRect(old.xMin, bounds.yMin, old.xMax, old.yMin)), 0);
        AddFace(faces, Rectangle(Rect.MinMaxRect(old.xMin, old.yMax, old.xMax, bounds.yMax)), 0);
        var roads = ReadRoads();
        int oldCount = (int)JObject.Parse(File.ReadAllText(Input + "layout_expanded.json"))["existingRoadCount"];
        var added = roads.Skip(oldCount).ToArray();
        foreach (var road in added) faces = Stamp(faces, Corridor(road, SidewalkWidth), 1, 0);
        foreach (var road in added) faces = Stamp(faces, Corridor(road, 0), 2, RoadY);
        Debug.Log($"GROUND_PARTITION faces={faces.Count} elapsed={watch.Elapsed.TotalSeconds:F2}s");
        var builder = BuildConforming(faces, bounds, out var topology);
        var ground = SaveMesh(builder.Build("Ground_Expanded", matrix.inverse), GroundPath);
        var markings = SaveMesh(BuildMarkings(roads, oldCount, faces, matrix.inverse, out int crossings), MarkingsPath);
        AssetDatabase.SaveAssets();
        Debug.Log("GROUND_TOPOLOGY " + JsonConvert.SerializeObject(topology));
        Verify(ground, matrix, new { topology, crossings, generationSeconds = watch.Elapsed.TotalSeconds });
        RenderPreviews(ground, markings, matrix, materials, SeamPoints(roads, oldCount));
        Debug.Log($"GROUND_GENERATE PASS ground={GroundPath} markings={MarkingsPath} seconds={watch.Elapsed.TotalSeconds:F2}");
    }

    private static Mesh SaveMesh(Mesh mesh, string path)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing == null) { AssetDatabase.CreateAsset(mesh, path); return mesh; }
        EditorUtility.CopySerialized(mesh, existing); UnityEngine.Object.DestroyImmediate(mesh);
        EditorUtility.SetDirty(existing); return existing;
    }

    private static (int, int) Cell(Vector3 p) => (Mathf.FloorToInt(p.x / 4), Mathf.FloorToInt(p.z / 4));

    private static MeshBuilder BuildConforming(List<Face> faces, Rect bounds, out object topology, bool repairSlivers = true)
    {
        // Previous Stage01-15 cuts contain sub-centimetre slivers. Merge only coincident XZ nodes,
        // keeping each face's original Y (asphalt and pavement share XZ at vertical curbs).
        var canonical = new Dictionary<(int, int), List<Vector3>>();
        Vector3 Canonical(Vector3 p)
        {
            int cx = Mathf.FloorToInt(p.x / .01f), cz = Mathf.FloorToInt(p.z / .01f);
            Vector3 best = p; float distance = .002f * .002f;
            for (int x = cx - 1; x <= cx + 1; x++)
                for (int z = cz - 1; z <= cz + 1; z++)
                    if (canonical.TryGetValue((x, z), out var list))
                        foreach (var q in list)
                        {
                            float d = (p.x - q.x) * (p.x - q.x) + (p.z - q.z) * (p.z - q.z);
                            if (d < distance) { distance = d; best = q; }
                        }
            if (distance < .002f * .002f) return new Vector3(best.x, p.y, best.z);
            var key = (cx, cz);
            if (!canonical.TryGetValue(key, out var bucket)) { bucket = new List<Vector3>(); canonical.Add(key, bucket); }
            best = new PointKey(p).Position; bucket.Add(best);
            return new Vector3(best.x, p.y, best.z);
        }
        var cleaned = new List<Face>();
        foreach (var face in faces)
        {
            var ring = new List<Vector3>();
            foreach (var p in face.Points)
            {
                var q = Canonical(p);
                if (ring.Count == 0 || !(new PointKey(q)).Equals(new PointKey(ring[ring.Count - 1]))) ring.Add(q);
            }
            if (ring.Count > 1 && (new PointKey(ring[0])).Equals(new PointKey(ring[ring.Count - 1]))) ring.RemoveAt(ring.Count - 1);
            AddFace(cleaned, ring, face.Slot);
        }
        faces = cleaned;
        var points = new Dictionary<PointKey, Vector3>();
        foreach (var face in faces)
            foreach (var p in face.Points) { var key = new PointKey(p); points[key] = key.Position; }
        var cells = new Dictionary<(int, int), List<Vector3>>();
        foreach (var p in points.Values)
        {
            var key = Cell(p);
            if (!cells.TryGetValue(key, out var bucket)) { bucket = new List<Vector3>(); cells.Add(key, bucket); }
            bucket.Add(p);
        }
        var builder = new MeshBuilder(4);
        var edges = new Dictionary<EdgeKey, List<EdgeSide>>();
        foreach (var face in faces)
        {
            var ring = new List<Vector3>();
            for (int i = 0; i < face.Points.Count; i++)
            {
                var a = face.Points[i]; var b = face.Points[(i + 1) % face.Points.Count];
                var pa = new PointKey(a).Position; var pb = new PointKey(b).Position;
                var d = pb - pa; float length2 = d.sqrMagnitude;
                if (length2 < 1e-12f) continue;
                var nodes = new Dictionary<PointKey, float> { [new PointKey(pa)] = 0 };
                var low = Cell(Vector3.Min(pa, pb) - Vector3.one * .001f);
                var high = Cell(Vector3.Max(pa, pb) + Vector3.one * .001f);
                for (int x = low.Item1; x <= high.Item1; x++)
                    for (int z = low.Item2; z <= high.Item2; z++)
                        if (cells.TryGetValue((x, z), out var bucket))
                            foreach (var p in bucket)
                            {
                                float t = Vector3.Dot(p - pa, d) / length2;
                                if (t * Mathf.Sqrt(length2) <= .0001f || (1 - t) * Mathf.Sqrt(length2) <= .0001f) continue;
                                if ((p - (pa + d * t)).sqrMagnitude <= 2.3e-8f) nodes[new PointKey(p)] = t;
                            }
                foreach (var node in nodes.OrderBy(n => n.Value))
                {
                    var p = node.Key.Position; p.y = Mathf.Lerp(a.y, b.y, node.Value); ring.Add(p);
                }
            }
            if (ring.Count < 3) continue;
            var center = face.Points.Aggregate(Vector3.zero, (p, q) => p + q) / face.Points.Count;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                builder.Triangle(center, a, b, face.Slot);
                var key = new EdgeKey(a, b);
                if (key.A.Equals(key.B)) continue;
                if (!edges.TryGetValue(key, out var sides)) { sides = new List<EdgeSide>(2); edges.Add(key, sides); }
                sides.Add(new EdgeSide { A = a, B = b, Slot = face.Slot });
            }
        }
        int curbEdges = 0, unmatched = 0, multiple = 0;
        var examples = new List<object>();
        foreach (var item in edges)
        {
            var sides = item.Value;
            if (sides.Count == 1)
            {
                var a = sides[0].A; var b = sides[0].B;
                bool Perimeter(Vector3 p) => Mathf.Abs(p.x - bounds.xMin) < .001f || Mathf.Abs(p.x - bounds.xMax) < .001f ||
                    Mathf.Abs(p.z - bounds.yMin) < .001f || Mathf.Abs(p.z - bounds.yMax) < .001f;
                if (!Perimeter(a) || !Perimeter(b))
                {
                    unmatched++; if (examples.Count < 40) examples.Add(new { a = V(a), b = V(b), sides = sides.Count });
                }
                continue;
            }
            if (sides.Count != 2) { multiple++; if (examples.Count < 60) examples.Add(new { a = V(sides[0].A), b = V(sides[0].B), sides = sides.Count }); continue; }
            var first = sides[0]; var second = sides[1];
            if ((new PointKey(first.A)).Equals(new PointKey(second.A)))
            { var swap = second.A; second.A = second.B; second.B = swap; }
            float dA = first.A.y - second.B.y, dB = first.B.y - second.A.y;
            if (Mathf.Max(Mathf.Abs(dA), Mathf.Abs(dB)) < .00015f) continue;
            var top = dA + dB > 0 ? first : second;
            var bottom = dA + dB > 0 ? second : first;
            builder.Triangle(top.A, bottom.B, top.B, 3);
            builder.Triangle(top.B, bottom.B, bottom.A, 3); curbEdges++;
        }
        // A Stage15 source sliver can collapse while welding. Close only small, closed interior
        // loops, using the adjacent material and heights. Large openings remain hard failures.
        int repaired = 0;
        if (repairSlivers && unmatched > 0 && multiple == 0)
        {
            var loose = edges.Values.Where(e => e.Count == 1).Select(e => e[0]).Where(e =>
                e.A.x > bounds.xMin + .001f && e.A.x < bounds.xMax - .001f &&
                e.A.z > bounds.yMin + .001f && e.A.z < bounds.yMax - .001f).ToList();
            while (loose.Count > 0)
            {
                var first = loose[0]; loose.RemoveAt(0);
                var ring = new List<Vector3> { first.B, first.A };
                while (!(new PointKey(ring[ring.Count - 1])).Equals(new PointKey(ring[0])) && ring.Count < 16)
                {
                    int next = loose.FindIndex(e => (new PointKey(e.B)).Equals(new PointKey(ring[ring.Count - 1])));
                    if (next < 0) break;
                    ring.Add(loose[next].A); loose.RemoveAt(next);
                }
                if (ring.Count < 4 || !(new PointKey(ring[0])).Equals(new PointKey(ring[ring.Count - 1]))) continue;
                ring.RemoveAt(ring.Count - 1);
                if (Area(ring) > 0 && Area(ring) < .0005)
                { AddFace(faces, ring, first.Slot); repaired++; }
            }
            if (repaired > 0)
            {
                Debug.Log("GROUND_IMPORT_SLIVERS repairedClosedLoops=" + repaired);
                return BuildConforming(faces, bounds, out topology, false);
            }
        }
        topology = new { planarVertices = points.Count, edges = edges.Count, curbEdges, unmatchedInteriorEdges = unmatched, nonManifoldEdges = multiple, examples };
        File.WriteAllText(Results + "codex_ground_topology.json", JsonConvert.SerializeObject(topology, Formatting.Indented));
        return builder;
    }

    private static float[] V(Vector3 p) => new[] { p.x, p.y, p.z };

    private static bool Intersection(Road a, Road b, out Vector3 point, out float ta, out float tb)
    {
        var d = a.B - a.A; var e = b.B - b.A;
        double denominator = (double)d.x * e.z - (double)d.z * e.x;
        point = default; ta = tb = 0;
        if (Math.Abs(denominator) < .00001) return false;
        var c = b.A - a.A;
        ta = (float)(((double)c.x * e.z - (double)c.z * e.x) / denominator);
        tb = (float)(((double)c.x * d.z - (double)c.z * d.x) / denominator);
        if (ta < -.001f || ta > 1.001f || tb < -.001f || tb > 1.001f) return false;
        point = a.A + d * ta; return true;
    }

    private static List<Vector3> SeamPoints(Road[] roads, int oldCount)
    {
        var result = new List<Vector3>();
        void Add(Vector3 p) { if (!result.Any(q => Vector3.Distance(p, q) < .2f)) result.Add(p); }
        foreach (var a in roads.Skip(oldCount))
            foreach (var b in roads.Take(oldCount))
            {
                if (Intersection(a, b, out var point, out _, out _)) Add(point);
                foreach (var p in new[] { a.A, a.B })
                {
                    float t = Mathf.Clamp01(Vector3.Dot(p - b.A, b.Direction) / b.Length);
                    if (Vector3.Distance(p, Vector3.Lerp(b.A, b.B, t)) < .03f) Add(p);
                }
            }
        return result;
    }

    private static Mesh BuildMarkings(Road[] roads, int oldCount, List<Face> ground, Matrix4x4 toLocal, out int crossings)
    {
        // Markings has the original white/yellow slot order. No overlays are emitted on pavement.
        var builder = new MeshBuilder(2);
        var asphalt = ground.Where(f => f.Slot == 2).ToArray();
        var junctions = new List<(Vector3, HashSet<Road>)>();
        for (int i = 0; i < roads.Length; i++)
            for (int j = Math.Max(i + 1, oldCount); j < roads.Length; j++)
                if (Intersection(roads[i], roads[j], out var p, out _, out _))
                {
                    int found = junctions.FindIndex(q => Vector3.Distance(q.Item1, p) < .5f);
                    if (found < 0) junctions.Add((p, new HashSet<Road> { roads[i], roads[j] }));
                    else { junctions[found].Item2.Add(roads[i]); junctions[found].Item2.Add(roads[j]); }
                }
        // Two meeting endpoints are a bend, not an intersection. Interior crossings have four approaches.
        junctions = junctions.Where(j => j.Item2.Sum(r =>
            (Vector3.Distance(j.Item1, r.A) > .05f ? 1 : 0) + (Vector3.Distance(j.Item1, r.B) > .05f ? 1 : 0)) >= 3).ToList();
        float Setback(Road road, HashSet<Road> incident)
        {
            float distance = road.Width * .5f + 2;
            foreach (var other in incident)
            {
                float sine = Mathf.Abs(road.Direction.x * other.Direction.z - road.Direction.z * other.Direction.x);
                if (sine > .15f) distance = Mathf.Max(distance, (other.Width * .5f + .3f) / sine + 1.8f);
            }
            return distance;
        }
        void Paint(List<Vector3> polygon, int slot)
        {
            var bounds = BoundsOf(polygon);
            foreach (var f in asphalt)
            {
                if (!Overlaps(bounds, f.Bounds)) continue;
                var clipped = Intersect(polygon, f.Points);
                if (clipped.Count >= 3 && Area(clipped) > 1e-8)
                    builder.Polygon(clipped.Select(p => new Vector3(p.x, RoadY + .012f, p.z)).ToList(), slot);
            }
        }
        List<Vector3> Stripe(Vector3 center, Vector3 along, Vector3 across, float length, float width)
        {
            var points = new List<Vector3> { center - along * length * .5f - across * width * .5f,
                center - along * length * .5f + across * width * .5f,
                center + along * length * .5f + across * width * .5f,
                center + along * length * .5f - across * width * .5f };
            if (Area(points) < 0) points.Reverse(); return points;
        }
        foreach (var road in roads.Skip(oldCount).Where(r => r.Width >= 5))
        {
            for (float distance = 2; distance < road.Length - 2; distance += 5)
            {
                var p = road.A + road.Direction * distance;
                if (junctions.Any(q => Vector3.Distance(q.Item1, p) < Setback(road, q.Item2) + 2.2f)) continue;
                Paint(Stripe(p, road.Direction, road.Normal, 1.7f, .12f), 1);
            }
        }
        crossings = 0;
        // Zebra bars run along each approach, spanning the road. This includes T junctions and narrow alleys.
        foreach (var junction in junctions)
        {
            foreach (var road in junction.Item2)
                foreach (int sign in new[] { -1, 1 })
                {
                    float at = Vector3.Dot(junction.Item1 - road.A, road.Direction) + sign * Setback(road, junction.Item2);
                    if (at < 1.6f || at > road.Length - 1.6f) continue;
                    var center = road.A + road.Direction * at;
                    for (float offset = -road.Width * .5f + .55f; offset < road.Width * .5f - .3f; offset += .8f)
                        Paint(Stripe(center + road.Normal * offset, road.Direction, road.Normal, 2.6f, .46f), 0);
                    crossings++;
                }
        }
        return builder.Build("Ground_ExpandedMarkings", toLocal);
    }

    private static void Verify(Mesh ground, Matrix4x4 matrix, object generation)
    {
        var roads = ReadRoads();
        int oldCount = (int)JObject.Parse(File.ReadAllText(Input + "layout_expanded.json"))["existingRoadCount"];
        var added = roads.Skip(oldCount).ToArray();
        var bounds = ReadBounds();
        var seams = SeamPoints(roads, oldCount);
        var vertices = ground.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
        double area = 0; int flipped = 0;
        for (int sub = 0; sub < 3; sub++)
        {
            var ids = ground.GetTriangles(sub); area += ProjectedArea(vertices, ids);
            for (int i = 0; i < ids.Length; i += 3)
                if (Vector3.Cross(vertices[ids[i + 1]] - vertices[ids[i]], vertices[ids[i + 2]] - vertices[ids[i]]).y < -1e-8f) flipped++;
        }
        var temporaryScene = EditorSceneManager.NewPreviewScene();
        temporaryScene.name = "Ground_Expanded_VerificationOnly";
        var owner = new GameObject("Ground_Expanded_VerificationOnly");
        var sourceOwner = new GameObject("Ground_Source_VerificationOnly");
        int missing = 0, wrongHeight = 0, wrongMaterial = 0, centerProbes = 0, sidewalkProbes = 0, seamProbes = 0, gridProbes = 0;
        int oldChanged = 0, oldProbes = 0, markingErrors = 0;
        SceneManager.MoveGameObjectToScene(owner, temporaryScene);
        SceneManager.MoveGameObjectToScene(sourceOwner, temporaryScene);
        var failures = new List<object>();
        try
        {
            // Test the saved mesh with its integration transform, not an independent height approximation.
            owner.transform.SetPositionAndRotation(matrix.MultiplyPoint3x4(Vector3.zero), matrix.rotation);
            owner.transform.localScale = matrix.lossyScale;
            var collider = owner.AddComponent<MeshCollider>(); collider.sharedMesh = ground;
            sourceOwner.transform.SetPositionAndRotation(owner.transform.position, owner.transform.rotation);
            sourceOwner.transform.localScale = owner.transform.localScale;
            var original = sourceOwner.AddComponent<MeshCollider>(); original.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(SourcePath);
            Physics.SyncTransforms();
            var ends = new List<int>(); int total = 0;
            for (int sub = 0; sub < 4; sub++) { total += ground.GetTriangles(sub).Length / 3; ends.Add(total); }
            void Probe(Vector3 p, float y, int expectedSlot, string category)
            {
                if (!collider.Raycast(new Ray(p + Vector3.up * 10, Vector3.down), out var hit, 20))
                { missing++; if (failures.Count < 200) failures.Add(new { category, reason = "missing", p = V(p) }); return; }
                int slot = ends.FindIndex(e => hit.triangleIndex < e);
                if (Mathf.Abs(hit.point.y - y) > .001f)
                { wrongHeight++; if (failures.Count < 200) failures.Add(new { category, reason = "height", p = V(p), expected = y, actual = hit.point.y }); }
                if (expectedSlot >= 0 && slot != expectedSlot)
                { wrongMaterial++; if (failures.Count < 200) failures.Add(new { category, reason = "material", p = V(p), expectedSlot, slot }); }
            }
            bool InRoad(Vector3 p) => added.Any(r => Inside(p, Corridor(r, 0)));
            foreach (var road in added)
            {
                int count = Mathf.CeilToInt(road.Length);
                for (int i = 0; i <= count; i++)
                { Probe(Vector3.Lerp(road.A, road.B, i / (float)count), RoadY, 2, road.Id); centerProbes++; }
                int walkCount = Mathf.CeilToInt(road.Length / 2);
                for (int i = 0; i <= walkCount; i++)
                    foreach (int sign in new[] { -1, 1 })
                        foreach (float walkOffset in new[] { .05f, .5f, 1f, 1.5f, 1.95f })
                        {
                            var p = Vector3.Lerp(road.A, road.B, i / (float)walkCount) + road.Normal * sign * (road.Width * .5f + walkOffset) + new Vector3(.00321f, 0, .00217f);
                            float expectedY = 0; int expectedSlot = 1;
                            if (InRoad(p)) { expectedY = RoadY; expectedSlot = 2; }
                            else if (original.Raycast(new Ray(p + Vector3.up * 10, Vector3.down), out var oldHit, 20))
                            {
                                expectedY = oldHit.point.y;
                                // Old asphalt/ramps are valid at junction mouths; preserve their exact heights.
                                if (expectedY < -.001f) expectedSlot = -1;
                            }
                            Probe(p, expectedY, expectedSlot, "sidewalk:" + road.Id); sidewalkProbes++;
                        }
            }
            foreach (var seam in seams)
                for (float dx = -3; dx <= 3; dx += .5f)
                    for (float dz = -3; dz <= 3; dz += .5f)
                    {
                        var p = seam + new Vector3(dx + .00321f, 0, dz + .00217f);
                        float y = 0;
                        if (InRoad(p)) y = RoadY;
                        else if (original.Raycast(new Ray(p + Vector3.up * 10, Vector3.down), out var h, 20)) y = h.point.y;
                        Probe(p, y, -1, "seam"); seamProbes++;
                    }
            // Independent whole-area coverage; the edge test alone cannot detect large uncovered regions.
            for (float x = bounds.xMin + .371f; x < bounds.xMax; x += 2)
                for (float z = bounds.yMin + .613f; z < bounds.yMax; z += 2)
                {
                    var p = new Vector3(x, 0, z);
                    if (!collider.Raycast(new Ray(p + Vector3.up * 10, Vector3.down), out _, 20)) missing++;
                    gridProbes++;
                    if (added.Any(r => Inside(p, Corridor(r, SidewalkWidth)))) continue;
                    if (!original.Raycast(new Ray(p + Vector3.up * 10, Vector3.down), out var before, 20)) continue;
                    oldProbes++;
                    if (!collider.Raycast(new Ray(p + Vector3.up * 10, Vector3.down), out var after, 20) || Mathf.Abs(before.point.y - after.point.y) > .001f) oldChanged++;
                }
            var painted = AssetDatabase.LoadAssetAtPath<Mesh>(MarkingsPath);
            if (painted == null) throw new Exception("Missing expanded marking mesh");
            var markVertices = painted.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
            var markIds = painted.triangles;
            for (int i = 0; i < markIds.Length; i += 3)
            {
                var a = markVertices[markIds[i]]; var b = markVertices[markIds[i + 1]]; var c = markVertices[markIds[i + 2]];
                var p = (a + b + c) / 3;
                if (Vector3.Cross(b - a, c - a).y < 0 ||
                    !collider.Raycast(new Ray(p + Vector3.up, Vector3.down), out var h, 2) ||
                    Mathf.Abs(h.point.y - RoadY) > .001f || Mathf.Abs(p.y - h.point.y - .012f) > .001f) markingErrors++;
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(owner); UnityEngine.Object.DestroyImmediate(sourceOwner);
            EditorSceneManager.ClosePreviewScene(temporaryScene);
        }
        // Rebuild topology from the saved triangle buffers, independent of generation's Face graph.
        var savedEdges = new Dictionary<EdgeKey, int>();
        for (int sub = 0; sub < 3; sub++)
        {
            var ids = ground.GetTriangles(sub);
            for (int i = 0; i < ids.Length; i += 3)
                for (int k = 0; k < 3; k++)
                {
                    var key = new EdgeKey(vertices[ids[i + k]], vertices[ids[i + (k + 1) % 3]]);
                    savedEdges.TryGetValue(key, out int count); savedEdges[key] = count + 1;
                }
        }
        bool Outer(EdgeKey edge)
        {
            var a = edge.A.Position; var b = edge.B.Position;
            return (Mathf.Abs(a.x - bounds.xMin) < .001f && Mathf.Abs(b.x - bounds.xMin) < .001f) ||
                (Mathf.Abs(a.x - bounds.xMax) < .001f && Mathf.Abs(b.x - bounds.xMax) < .001f) ||
                (Mathf.Abs(a.z - bounds.yMin) < .001f && Mathf.Abs(b.z - bounds.yMin) < .001f) ||
                (Mathf.Abs(a.z - bounds.yMax) < .001f && Mathf.Abs(b.z - bounds.yMax) < .001f);
        }
        int boundaryMismatch = savedEdges.Count(e => e.Value == 1 && !Outer(e.Key));
        int nonManifold = savedEdges.Count(e => e.Value > 2);
        double physicalArea = 0;
        var allIds = ground.triangles;
        for (int i = 0; i < allIds.Length; i += 3)
            physicalArea += Vector3.Cross(vertices[allIds[i + 1]] - vertices[allIds[i]], vertices[allIds[i + 2]] - vertices[allIds[i]]).magnitude * .5;
        double expectedArea = bounds.width * bounds.height;
        bool passed = Math.Abs(area - expectedArea) < .1 && flipped == 0 && missing == 0 && wrongHeight == 0 && wrongMaterial == 0 &&
            oldChanged == 0 && boundaryMismatch == 0 && nonManifold == 0 && markingErrors == 0;
        var report = new
        {
            passed, generation, expectedArea, projectedTopArea = area, areaError = area - expectedArea, physicalAreaIncludingVerticalCurbs = physicalArea,
            vertices = ground.vertexCount, triangles = ground.triangles.Length / 3,
            bytes = new FileInfo(GroundPath).Length, bounds = ground.bounds.ToString(),
            centerProbes, sidewalkProbes, seamCount = seams.Count, seamProbes, gridProbes, missing, wrongHeight, wrongMaterial,
            boundaryMismatch, nonManifold, flippedTriangles = flipped, markingErrors, preservedOldProbes = oldProbes, oldChanged,
            sampleBoundaryOffset = new[] { .00321f, .00217f },
            seams = seams.Select(V).ToArray(), failures,
        };
        File.WriteAllText(Results + "codex_ground_verification.json", JsonConvert.SerializeObject(report, Formatting.Indented));
        Debug.Log("GROUND_VERIFY " + JsonConvert.SerializeObject(report));
        if (!passed) throw new Exception("GROUND_VERIFY FAIL: see codex_ground_verification.json");
    }

    private static bool Inside(Vector3 p, List<Vector3> polygon)
    {
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            if ((double)(b.z - a.z) * (p.x - a.x) - (double)(b.x - a.x) * (p.z - a.z) < -.0001) return false;
        }
        return true;
    }

    private static void RenderPreviews(Mesh ground, Mesh markings, Matrix4x4 matrix, Material[] materials, List<Vector3> seams)
    {
        var colors = materials.Select(m => m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.color).ToArray();
        Raster(ground, markings, matrix, colors, ReadBounds(), 2320, Results + "바닥_부감.png");
        var selected = new[] { 0, 5, 7, 9, 10 }.Where(i => i < seams.Count).ToArray();
        for (int i = 0; i < selected.Length; i++)
        {
            var p = seams[selected[i]];
            Raster(ground, markings, matrix, colors, new Rect(p.x - 12, p.z - 12, 24, 24), 1200, Results + $"바닥_이음부_{i + 1:D2}.png");
        }
        Raster(ground, markings, matrix, colors, new Rect(-73, 20, 48, 24), 1800, Results + "바닥_사선.png");
    }

    // Orthographic raster of the actual saved triangles. Works in batchmode without a display or Scene changes.
    private static void Raster(Mesh ground, Mesh markings, Matrix4x4 matrix, Color[] colors, Rect bounds, int width, string path)
    {
        int height = Mathf.RoundToInt(width * bounds.height / bounds.width);
        var pixels = Enumerable.Repeat(new Color32(229, 35, 145, 255), width * height).ToArray();
        void Draw(Mesh mesh, Color[] palette)
        {
            var vertices = mesh.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var ids = mesh.GetTriangles(sub);
                var color = (Color32)palette[sub];
                for (int k = 0; k < ids.Length; k += 3)
                {
                    var p = vertices[ids[k]]; var q = vertices[ids[k + 1]]; var r = vertices[ids[k + 2]];
                    if (Vector3.Cross(q - p, r - p).y <= 1e-10f) continue;
                    Vector2 Pixel(Vector3 v) => new Vector2((v.x - bounds.xMin) / bounds.width * width, (v.z - bounds.yMin) / bounds.height * height);
                    var a = Pixel(p); var b = Pixel(q); var c = Pixel(r);
                    float Cross(Vector2 x, Vector2 y) => x.x * y.y - x.y * y.x;
                    float denominator = Cross(b - a, c - a);
                    int xMin = Math.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, b.x, c.x)));
                    int xMax = Math.Min(width - 1, Mathf.CeilToInt(Mathf.Max(a.x, b.x, c.x)));
                    int yMin = Math.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, b.y, c.y)));
                    int yMax = Math.Min(height - 1, Mathf.CeilToInt(Mathf.Max(a.y, b.y, c.y)));
                    for (int y = yMin; y <= yMax; y++)
                        for (int x = xMin; x <= xMax; x++)
                        {
                            var v = new Vector2(x + .5f, y + .5f);
                            float u = Cross(b - v, c - v) / denominator;
                            float t = Cross(c - v, a - v) / denominator;
                            if (u >= -.00001f && t >= -.00001f && u + t <= 1.00001f) pixels[y * width + x] = color;
                        }
                }
            }
        }
        Draw(ground, colors);
        Draw(markings, new[] { Color.white, new Color(1, .76f, .12f) });
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.SetPixels32(pixels); texture.Apply(); File.WriteAllBytes(path, texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);
    }
}
