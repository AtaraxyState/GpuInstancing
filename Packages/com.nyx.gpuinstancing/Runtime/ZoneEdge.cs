using System.Collections.Generic;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Bord de la zone côté CPU, même calcul que NyxZoneEdge (InstancingZone.hlsl) côté GPU : règle pair-impair, ou distance
    /// signée au polygone + bruit fractal pour un bord naturel. Pour VolumeContains, l'éditeur et les tests ; le placement et
    /// la destruction se font sur GPU.
    /// </summary>
    public static class ZoneEdge
    {
        /// <summary>
        /// Bord en (x, z) : &lt;= 0 dedans. Sans bord naturel : -1 ou 1, règle pair-impair (un point pile sur un bord peut
        /// compter comme dehors). Avec : distance signée au polygone (m) + amplitude × bruit fractal, mesuré depuis 'origin'.
        /// </summary>
        public static float Evaluate(Vector2 p, IReadOnlyList<Vector2> polygon, float amplitude, float inverseScale, bool natural, Vector2 origin, uint seed)
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
                edge += amplitude * ValueNoise.Fbm3((p - origin) * inverseScale, seed);
            return edge;
        }
    }
}
