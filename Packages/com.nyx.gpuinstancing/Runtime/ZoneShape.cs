using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Forme de la zone d'un MeshSpawner, côté C# (même calcul que InstancingZone.hlsl, côté GPU) : contour courbe passant
    /// par les points de contrôle, bord naturel (distance signée au polygone + bruit fractal). Pour VolumeContains et
    /// l'éditeur ; le placement et la destruction se font sur GPU.
    /// </summary>
    public static class ZoneShape
    {
        /// <summary>
        /// Contour courbe fermé passant par chaque point de contrôle (Catmull-Rom centripète : ni boucle ni dépassement aux
        /// points rapprochés), chaque segment découpé pour obtenir au plus maxVertices sommets.
        /// </summary>
        public static List<Vector2> Smooth(IReadOnlyList<Vector2> control, int maxVertices)
        {
            int n = control.Count;
            var result = new List<Vector2>(maxVertices);
            int steps = Mathf.Max(1, maxVertices / Mathf.Max(n, 1));
            if (n < 3 || steps == 1)
            {
                for (int i = 0; i < n; i++)
                    result.Add(control[i]);
                return result;
            }
            for (int i = 0; i < n; i++)
            {
                Vector2 p0 = control[(i + n - 1) % n], p1 = control[i], p2 = control[(i + 1) % n], p3 = control[(i + 2) % n];
                float t0 = 0f;
                float t1 = t0 + Knot(p0, p1);
                float t2 = t1 + Knot(p1, p2);
                float t3 = t2 + Knot(p2, p3);
                for (int s = 0; s < steps; s++)
                {
                    float t = Mathf.Lerp(t1, t2, (float)s / steps);
                    Vector2 a1 = Mix(p0, p1, t0, t1, t), a2 = Mix(p1, p2, t1, t2, t), a3 = Mix(p2, p3, t2, t3, t);
                    Vector2 b1 = Mix(a1, a2, t0, t2, t), b2 = Mix(a2, a3, t1, t3, t);
                    result.Add(Mix(b1, b2, t1, t2, t));
                }
            }
            return result;
        }

        static float Knot(Vector2 a, Vector2 b) => Mathf.Max(Mathf.Sqrt((b - a).magnitude), 1e-3f);   // alpha = 0,5

        static Vector2 Mix(Vector2 a, Vector2 b, float ta, float tb, float t) => (a * (tb - t) + b * (t - ta)) / (tb - ta);

        /// <summary>
        /// Bord en (x, z) : &lt;= 0 dedans. Sans bord naturel (amplitude et lisière nulles) : -1 ou 1, règle pair-impair.
        /// Avec : distance signée au polygone (m) + amplitude × bruit fractal (= NyxZoneEdge, InstancingZone.hlsl).
        /// </summary>
        public static float Edge(Vector2 p, IReadOnlyList<Vector2> polygon, float amplitude, float inverseScale, bool natural, Vector2 origin, uint seed)
        {
            bool inside = false;
            float distanceSq = float.MaxValue;
            Vector2 a = polygon[polygon.Count - 1];
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 b = polygon[i];
                if ((b.y > p.y) != (a.y > p.y) && p.x < (a.x - b.x) * (p.y - b.y) / (a.y - b.y) + b.x)
                    inside = !inside;
                if (natural)
                {
                    Vector2 e = a - b;
                    Vector2 w = p - b;
                    Vector2 c = w - e * Mathf.Clamp01(Vector2.Dot(w, e) / Mathf.Max(Vector2.Dot(e, e), 1e-12f));
                    distanceSq = Mathf.Min(distanceSq, Vector2.Dot(c, c));
                }
                a = b;
            }
            if (!natural)
                return inside ? -1f : 1f;
            float edge = (inside ? -1f : 1f) * Mathf.Sqrt(distanceSq);
            if (amplitude > 0f)
                edge += amplitude * Fbm((p - origin) * inverseScale, seed);
            return edge;
        }

        /// <summary>Bruit fractal 3 octaves dans [-1, 1] (= NyxZoneFbm).</summary>
        public static float Fbm(Vector2 p, uint seed)
        {
            float sum = 0f;
            float amplitude = 0.5f;
            for (uint o = 0; o < 3; o++)
            {
                sum += ValueNoise(p, seed + o) * amplitude;
                amplitude *= 0.5f;
                p = p * 2.03f + new Vector2(17.1f, 17.1f);
            }
            return sum / 0.875f;
        }

        static float ValueNoise(Vector2 p, uint seed)
        {
            float ix = Mathf.Floor(p.x), iy = Mathf.Floor(p.y);
            float fx = p.x - ix, fy = p.y - iy;
            float ux = fx * fx * (3f - 2f * fx), uy = fy * fy * (3f - 2f * fy);
            int cx = (int)ix, cy = (int)iy;
            float a = Hash01(cx, cy, seed), b = Hash01(cx + 1, cy, seed), c = Hash01(cx, cy + 1, seed), d = Hash01(cx + 1, cy + 1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, ux), Mathf.Lerp(c, d, ux), uy) * 2f - 1f;
        }

        static float Hash01(int x, int y, uint seed)
        {
            uint h = math.hash(new uint3(unchecked((uint)x), unchecked((uint)y), seed));
            h ^= h >> 15;
            h = unchecked(h * 0x2c1b3c6du);
            h ^= h >> 12;
            return (h & 0xFFFFFFu) / 16777215f;
        }

        /// <summary>
        /// Contour (x, z) où le bord vaut 0, en segments (2 points par segment), par carrés marchants sur une grille de
        /// resolution × resolution cases couvrant 'bounds' (min x, min z, max x, max z). Pour dessiner le bord naturel.
        /// </summary>
        public static List<Vector2> Contour(System.Func<Vector2, float> edge, Vector4 bounds, int resolution)
        {
            var segments = new List<Vector2>();
            int n = resolution + 1;
            var values = new float[n * n];
            float dx = (bounds.z - bounds.x) / resolution, dz = (bounds.w - bounds.y) / resolution;
            for (int z = 0; z < n; z++)
            {
                for (int x = 0; x < n; x++)
                    values[z * n + x] = edge(new Vector2(bounds.x + x * dx, bounds.y + z * dz));
            }
            var crossings = new List<Vector2>(4);
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    crossings.Clear();
                    Vector2 p00 = new Vector2(bounds.x + x * dx, bounds.y + z * dz);
                    float v00 = values[z * n + x], v10 = values[z * n + x + 1], v01 = values[(z + 1) * n + x], v11 = values[(z + 1) * n + x + 1];
                    Cross(p00, p00 + new Vector2(dx, 0f), v00, v10, crossings);
                    Cross(p00 + new Vector2(dx, 0f), p00 + new Vector2(dx, dz), v10, v11, crossings);
                    Cross(p00 + new Vector2(dx, dz), p00 + new Vector2(0f, dz), v11, v01, crossings);
                    Cross(p00 + new Vector2(0f, dz), p00, v01, v00, crossings);
                    for (int i = 0; i + 1 < crossings.Count; i += 2)
                    {
                        segments.Add(crossings[i]);
                        segments.Add(crossings[i + 1]);
                    }
                }
            }
            return segments;
        }

        static void Cross(Vector2 a, Vector2 b, float va, float vb, List<Vector2> crossings)
        {
            if ((va <= 0f) == (vb <= 0f))
                return;
            crossings.Add(Vector2.Lerp(a, b, va / (va - vb)));
        }
    }
}
