#ifndef NYX_INSTANCING_NOISE_INCLUDED
#define NYX_INSTANCING_NOISE_INCLUDED

// Bruit de valeur 2D (cellules d'un mètre) et bruit fractal. Mêmes résultats en C# (ValueNoise.cs) : le bord naturel de la
// zone est le même sur GPU (placement, destruction) et sur CPU (VolumeContains, éditeur).

#include "InstancingRandom.hlsl"

// Valeur de la cellule dans [0, 1] (= ValueNoise.Hash01).
float NyxNoiseHash01(int2 cell, uint seed)
{
    uint h = NyxHash3(uint3(asuint(cell.x), asuint(cell.y), seed));
    h ^= h >> 15;
    h *= 0x2c1b3c6du;
    h ^= h >> 12;
    return (h & 0xFFFFFFu) / 16777215.0;
}

// Bruit de valeur dans [-1, 1], interpolation lissée entre les cellules (= ValueNoise.Sample).
float NyxValueNoise(float2 p, uint seed)
{
    float2 i = floor(p);
    float2 f = p - i;
    float2 u = f * f * (3.0 - 2.0 * f);
    int2 c = (int2)i;
    float a = NyxNoiseHash01(c, seed);
    float b = NyxNoiseHash01(c + int2(1, 0), seed);
    float d = NyxNoiseHash01(c + int2(0, 1), seed);
    float e = NyxNoiseHash01(c + int2(1, 1), seed);
    return lerp(lerp(a, b, u.x), lerp(d, e, u.x), u.y) * 2.0 - 1.0;
}

// Bruit fractal, 3 octaves, dans [-1, 1] (= ValueNoise.Fbm3).
float NyxFbm3(float2 p, uint seed)
{
    float sum = 0;
    float amplitude = 0.5;
    [unroll]
    for (uint o = 0; o < 3; o++)
    {
        sum += NyxValueNoise(p, seed + o) * amplitude;
        amplitude *= 0.5;
        p = p * 2.03 + 17.1;
    }
    return sum / 0.875;
}

#endif // NYX_INSTANCING_NOISE_INCLUDED
