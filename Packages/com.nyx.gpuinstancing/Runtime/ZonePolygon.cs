using System.Collections.Generic;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Construction du polygone de la zone : contour courbe passant par les points de contrôle, boîte, reconnaissance d'un
    /// rectangle aligné sur les axes. Calculs purs (X, Z), sans état.
    /// </summary>
    public static class ZonePolygon
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

        /// <summary>Boîte des sommets : min x, min z, max x, max z.</summary>
        public static Vector4 Bounds(IReadOnlyList<Vector2> polygon)
        {
            var bounds = new Vector4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            foreach (Vector2 p in polygon)
                bounds = new Vector4(Mathf.Min(bounds.x, p.x), Mathf.Min(bounds.y, p.y), Mathf.Max(bounds.z, p.x), Mathf.Max(bounds.w, p.y));
            return bounds;
        }

        /// <summary>Quatre sommets aux coins de leur boîte, reliés par des arêtes parallèles aux axes (pas un nœud papillon).</summary>
        public static bool IsAxisAlignedRectangle(IReadOnlyList<Vector2> polygon, Vector4 bounds)
        {
            if (polygon.Count != 4)
                return false;
            int corners = 0;
            for (int i = 0; i < 4; i++)
            {
                Vector2 p = polygon[i];
                Vector2 next = polygon[(i + 1) & 3];
                bool cornerX = p.x == bounds.x || p.x == bounds.z;
                bool cornerZ = p.y == bounds.y || p.y == bounds.w;
                if (!cornerX || !cornerZ || (p.x != next.x && p.y != next.y))
                    return false;
                corners |= 1 << ((p.x == bounds.z ? 1 : 0) + (p.y == bounds.w ? 2 : 0));
            }
            return corners == 15;
        }
    }
}
