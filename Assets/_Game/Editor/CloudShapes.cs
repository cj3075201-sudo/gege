using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DeliveryRider.Editor
{
    // Soft cumulus meshes for the near clouds, after AlexStrook/UnlitClouds (CC0): a heap of overlapping, smooth
    // squashed spheres (a row of large puffs, smaller ones on top and low ones at the sides) with a flat base, about
    // 1 long, 0.5 high and 0.45 deep. Each vertex carries an occlusion value in its colour (1 open, 0 hidden): the
    // underside and the gaps between puffs are darker, where the original bakes its whole lighting. The second UV
    // channel holds the vertex's puff centre (xyz) and the puff's turn to form (w, 0 first): Cloud.shader swells the
    // puffs up one by one as a cloud forms, the big middle ones first, and lets them sink back the other way round as
    // it breaks up. Built once into
    // Art/Clouds; "Rebuild Cloud Shapes" rebuilds them in place after changing this file. Replace them with modelled
    // clouds of the same size (white vertex colours) at any time. The rider's walking dust uses much smaller heaps of
    // the same kind.
    public static class CloudShapes
    {
        private const string Folder = "Assets/_Game/Art/Clouds";
        private const int Count = 4;
        // Each puff is an icosphere subdivided this many times (642 corners): round silhouettes at the distances the
        // clouds are seen from.
        private const int Subdivisions = 3;
        private const int DustCount = 3;
        // Dust is small, short-lived and several at once: 162 corners a puff, three puffs a heap (960 triangles).
        private const int DustSubdivisions = 2;
        // How dark the gaps between puffs are (1 not at all). Dust is seen close up, where the clouds' dark gaps turn
        // its puffs into separate balls.
        private const float CloudGapShade = 0.45f;
        private const float DustGapShade = 0.85f;

        private struct Puff
        {
            public Vector3 centre, radii;
            public float turn;
        }

        public static Mesh[] LoadOrBuild() => LoadOrBuild("CloudShape", Count, Build);

        public static Mesh[] LoadOrBuildDust() => LoadOrBuild("DustPuff", DustCount, BuildDust);

        private static Mesh[] LoadOrBuild(string name, int count, System.Func<int, Mesh, Mesh> build)
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/_Game/Art", "Clouds");
            var shapes = new Mesh[count];
            for (int i = 0; i < count; i++)
            {
                string path = $"{Folder}/{name}_{i}.asset";
                shapes[i] = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (shapes[i] != null) continue;
                shapes[i] = build(Seed(i), new Mesh());
                shapes[i].name = $"{name}_{i}";
                AssetDatabase.CreateAsset(shapes[i], path);
            }
            return shapes;
        }

        [MenuItem("Delivery Rider/Rebuild Cloud Shapes")]
        public static void Rebuild()
        {
            var shapes = LoadOrBuild();
            for (int i = 0; i < shapes.Length; i++) Save(Build(Seed(i), shapes[i]));
            var dust = LoadOrBuildDust();
            for (int i = 0; i < dust.Length; i++) Save(BuildDust(Seed(i), dust[i]));
            Debug.Log($"Rebuilt {shapes.Length} cloud shapes and {dust.Length} dust puffs in {Folder}.");
        }

        private static void Save(Mesh mesh)
        {
            EditorUtility.SetDirty(mesh);
            AssetDatabase.SaveAssetIfDirty(mesh);
        }

        private static int Seed(int index) => 17 + index * 31;

        // A small heap, about 1 wide and 0.55 high: one big puff first, then a smaller one low on each side. Two equal
        // puffs side by side read as a peanut.
        private static Mesh BuildDust(int seed, Mesh mesh)
        {
            var random = new System.Random(seed);
            float Range(float min, float max) => Mathf.Lerp(min, max, (float)random.NextDouble());
            float big = Range(0.32f, 0.36f), left = Range(0.18f, 0.23f), right = Range(0.16f, 0.21f);
            var puffs = new List<Puff>
            {
                new Puff { centre = new Vector3(Range(-0.03f, 0.03f), big * 0.65f, 0f), radii = new Vector3(big, big * 0.95f, big), turn = 0f },
                new Puff { centre = new Vector3(-0.28f, left * 0.55f, Range(0.04f, 0.12f)), radii = new Vector3(left, left * 0.9f, left), turn = 0.4f },
                new Puff { centre = new Vector3(0.29f, right * 0.55f, Range(-0.12f, -0.04f)), radii = new Vector3(right, right * 0.9f, right), turn = 0.65f },
            };
            return Assemble(puffs, DustSubdivisions, random, mesh, DustGapShade);
        }

        private static Mesh Build(int seed, Mesh mesh)
        {
            var random = new System.Random(seed);
            float Next() => (float)random.NextDouble();
            float Range(float min, float max) => Mathf.Lerp(min, max, Next());
            var puffs = new List<Puff>();
            // The row: bigger puffs in the middle make the heap.
            int row = 5 + random.Next(3);
            for (int p = 0; p < row; p++)
            {
                float along = p / (float)(row - 1);
                float radius = Mathf.Lerp(0.12f, 0.22f, 1f - Mathf.Abs(along - 0.5f) * 1.4f) * Range(0.85f, 1.15f);
                puffs.Add(new Puff
                {
                    centre = new Vector3(Mathf.Lerp(-0.36f, 0.36f, along) + Range(-0.03f, 0.03f), radius * 0.45f + Range(0f, 0.04f), Range(-0.06f, 0.06f)),
                    radii = new Vector3(radius, radius * 0.85f, radius * 0.9f),
                    turn = Mathf.Abs(along - 0.5f) * 0.7f,
                });
            }
            // Smaller puffs piled on the middle of the row.
            int tops = 2 + random.Next(3);
            for (int t = 0; t < tops; t++)
            {
                var under = puffs[1 + random.Next(row - 2)];
                float radius = under.radii.x * Range(0.55f, 0.75f);
                puffs.Add(new Puff
                {
                    centre = under.centre + new Vector3(Range(-0.06f, 0.06f), under.radii.y * Range(0.55f, 0.8f), Range(-0.05f, 0.05f)),
                    radii = new Vector3(radius, radius * 0.9f, radius * 0.9f),
                    turn = Range(0.4f, 0.7f),
                });
            }
            // Low puffs at the front and back widen the base.
            int sides = 2 + random.Next(3);
            for (int s = 0; s < sides; s++)
            {
                float radius = Range(0.09f, 0.14f);
                puffs.Add(new Puff
                {
                    centre = new Vector3(Range(-0.28f, 0.28f), radius * 0.4f, (s % 2 == 0 ? 1f : -1f) * Range(0.1f, 0.15f)),
                    radii = new Vector3(radius * 1.2f, radius * 0.8f, radius),
                    turn = Range(0.45f, 0.9f),
                });
            }
            return Assemble(puffs, Subdivisions, random, mesh, CloudGapShade);
        }

        private static Mesh Assemble(List<Puff> puffs, int subdivisions, System.Random random, Mesh mesh, float gapShade)
        {
            float Range(float min, float max) => Mathf.Lerp(min, max, (float)random.NextDouble());
            float top = 0f;
            foreach (var puff in puffs) top = Mathf.Max(top, puff.centre.y + puff.radii.y);

            Icosphere(subdivisions, out var sphere, out var faces);
            var vertices = new List<Vector3>();
            var colors = new List<Color32>();
            var growth = new List<Vector4>();
            var triangles = new List<int>();
            for (int p = 0; p < puffs.Count; p++)
            {
                int first = vertices.Count;
                var radii = puffs[p].radii;
                foreach (var direction in sphere)
                {
                    var corner = puffs[p].centre + Vector3.Scale(direction, radii) * Range(0.98f, 1.02f);
                    // The surface normal of the squashed sphere; the flat base faces down.
                    var normal = corner.y > 0f ? new Vector3(direction.x / radii.x, direction.y / radii.y, direction.z / radii.z).normalized : Vector3.down;
                    corner.y = Mathf.Max(corner.y, 0f);
                    byte open = (byte)Mathf.RoundToInt(Openness(puffs, p, corner, normal, top, gapShade) * 255f);
                    vertices.Add(corner);
                    colors.Add(new Color32(open, open, open, 255));
                    growth.Add(new Vector4(puffs[p].centre.x, puffs[p].centre.y, puffs[p].centre.z, puffs[p].turn));
                }
                foreach (var face in faces)
                {
                    Vector3 a = vertices[first + face.x], b = vertices[first + face.y], c = vertices[first + face.z];
                    // Faces pressed flat into the base vanish.
                    if (Vector3.Cross(b - a, c - a).sqrMagnitude < 1e-12f) continue;
                    triangles.Add(first + face.x);
                    triangles.Add(first + face.y);
                    triangles.Add(first + face.z);
                }
            }
            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetUVs(1, growth);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Lower points, points turned down and points close to (or inside) a neighbouring puff are less open. The
        // upper part stays fully open, so the heap keeps a white top. The flat bases of overlapping puffs share one
        // plane, so they all take the same value there; anything else would flicker when seen from below.
        private static float Openness(List<Puff> puffs, int own, Vector3 point, Vector3 normal, float top, float gapShade)
        {
            float height = Mathf.Lerp(0.45f, 1f, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(point.y / (top * 0.6f))));
            float facing = Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(normal.y + 0.6f));
            if (point.y <= 0f) return height * facing;
            float crowd = 0f;
            for (int q = 0; q < puffs.Count; q++)
            {
                if (q == own) continue;
                var radii = puffs[q].radii;
                float reach = Vector3.Scale(point - puffs[q].centre, new Vector3(1f / radii.x, 1f / radii.y, 1f / radii.z)).magnitude;
                crowd += Mathf.Clamp01((1.35f - reach) / 0.35f);
            }
            return height * facing * Mathf.Lerp(1f, gapShade, Mathf.Clamp01(crowd));
        }

        // Unit icosphere, every face wound to face outward.
        private static void Icosphere(int subdivisions, out List<Vector3> points, out List<Vector3Int> faces)
        {
            float t = (1f + Mathf.Sqrt(5f)) * 0.5f;
            var list = new List<Vector3>
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
            };
            for (int i = 0; i < list.Count; i++) list[i] = list[i].normalized;
            int[] start =
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            };
            faces = new List<Vector3Int>();
            for (int i = 0; i < start.Length; i += 3) faces.Add(new Vector3Int(start[i], start[i + 1], start[i + 2]));
            var middles = new Dictionary<long, int>();
            int Middle(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (middles.TryGetValue(key, out int index)) return index;
                list.Add(((list[a] + list[b]) * 0.5f).normalized);
                middles[key] = list.Count - 1;
                return list.Count - 1;
            }
            for (int level = 0; level < subdivisions; level++)
            {
                var finer = new List<Vector3Int>(faces.Count * 4);
                foreach (var face in faces)
                {
                    int ab = Middle(face.x, face.y), bc = Middle(face.y, face.z), ca = Middle(face.z, face.x);
                    finer.Add(new Vector3Int(face.x, ab, ca));
                    finer.Add(new Vector3Int(face.y, bc, ab));
                    finer.Add(new Vector3Int(face.z, ca, bc));
                    finer.Add(new Vector3Int(ab, bc, ca));
                }
                faces = finer;
            }
            // Unity draws the side where cross(b - a, c - a) points; turn every face outward.
            for (int i = 0; i < faces.Count; i++)
            {
                var face = faces[i];
                Vector3 a = list[face.x], b = list[face.y], c = list[face.z];
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), a + b + c) < 0f) faces[i] = new Vector3Int(face.x, face.z, face.y);
            }
            points = list;
        }
    }
}
