using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nyx.GpuInstancing.Editor
{
    /// <summary>Tracé d'un bord par carrés marchants : contour où une fonction vaut 0. Pour dessiner le bord naturel de la zone.</summary>
    static class ZoneContour
    {
        /// <summary>
        /// Contour (x, z) où 'edge' vaut 0, en segments (2 points par segment), sur une grille de resolution × resolution
        /// cases couvrant 'bounds' (min x, min z, max x, max z).
        /// </summary>
        public static List<Vector2> Trace(Func<Vector2, float> edge, Vector4 bounds, int resolution)
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
