using Unity.Mathematics;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Bruit de valeur 2D (cellules d'un mètre) et bruit fractal, mêmes résultats que InstancingNoise.hlsl sur GPU : le bord
    /// naturel de la zone est le même pour le placement et la destruction (GPU) que pour VolumeContains et l'éditeur (CPU).
    /// </summary>
    internal static class ValueNoise
    {
        /// <summary>Bruit fractal, 3 octaves, dans [-1, 1] (= NyxFbm3).</summary>
        public static float Fbm3(Vector2 p, uint seed)
        {
            float sum = 0f;
            float amplitude = 0.5f;
            for (uint o = 0; o < 3; o++)
            {
                sum += Sample(p, seed + o) * amplitude;
                amplitude *= 0.5f;
                p = p * 2.03f + new Vector2(17.1f, 17.1f);
            }
            return sum / 0.875f;
        }

        /// <summary>Bruit de valeur dans [-1, 1], interpolation lissée entre les cellules (= NyxValueNoise).</summary>
        public static float Sample(Vector2 p, uint seed)
        {
            float ix = Mathf.Floor(p.x), iy = Mathf.Floor(p.y);
            float fx = p.x - ix, fy = p.y - iy;
            float ux = fx * fx * (3f - 2f * fx), uy = fy * fy * (3f - 2f * fy);
            int cx = (int)ix, cy = (int)iy;
            float a = Hash01(cx, cy, seed), b = Hash01(cx + 1, cy, seed), c = Hash01(cx, cy + 1, seed), d = Hash01(cx + 1, cy + 1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, ux), Mathf.Lerp(c, d, ux), uy) * 2f - 1f;
        }

        /// <summary>Valeur de la cellule dans [0, 1] (= NyxNoiseHash01).</summary>
        static float Hash01(int x, int y, uint seed)
        {
            uint h = math.hash(new uint3(unchecked((uint)x), unchecked((uint)y), seed));
            h ^= h >> 15;
            h = unchecked(h * 0x2c1b3c6du);
            h ^= h >> 12;
            return (h & 0xFFFFFFu) / 16777215f;
        }
    }
}
