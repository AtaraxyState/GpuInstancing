#ifndef NYX_INSTANCING_ZONE_INCLUDED
#define NYX_INSTANCING_ZONE_INCLUDED

// Volume de la zone d'un MeshSpawner : polygone (face du haut, x et z monde) extrudé vers le bas. Même test pour le
// placement (InstanceBuild.compute) et pour la destruction pendant le culling (InstancingCulling.hlsl), et en C#
// (MeshSpawner.VolumeContains, ZoneShape.cs).
//
// Bord naturel (facultatif, _NyxZoneNatural.x > 0) : le bord est déplacé de ± amplitude par un bruit fractal (3 octaves),
// mesuré depuis le centre de la zone : distance signée au polygone + bruit <= 0. Coût mesuré : 0,75 ms pour 1 000 000
// de points avec 64 sommets (GTX 1060), contre 8 ms pour un contour décrit par 1 024 sommets.
// Lisière (placement procédural seulement) : la densité baisse sur _NyxZoneNatural.z mètres avant le bord.

#include "InstancingRandom.hlsl"

// Sommets dans un buffer (MeshSpawner.MaxZonePoints au plus) : le coût du test croît avec leur nombre (pour 1 000 000 de
// tests, GTX 1060 : 64 sommets 0,7 ms, 1 024 sommets 8 ms). Préférer quelques points + bord naturel aux longues listes.
StructuredBuffer<float2> _NyxZoneVertices;         // sommets monde (x, z), dans l'ordre du contour
uint   _NyxZonePointCount;                         // nombre de sommets (culling : 0 = pas de destruction par le volume)
float4 _NyxZoneBounds;                             // boîte du polygone, agrandie de l'amplitude du bord : min x, min z, max x, max z
float4 _NyxZoneHeight;                             // x : bas, y : haut du volume (monde, tolérance comprise)
float4 _NyxZoneNatural;                            // x : amplitude du bord (m), y : 1 / taille des irrégularités, z : lisière (m)
float4 _NyxZoneOrigin;                             // xy : centre de la zone (x, z), z : graine du bruit (entier)

float2 NyxZoneVertex(uint i)
{
    return _NyxZoneVertices[i];
}

// Bruit de valeur 2D dans [-1, 1], cellules d'un mètre (= ZoneShape.ValueNoise, C#).
float NyxZoneHash01(int2 cell, uint seed)
{
    uint h = NyxHash3(uint3(asuint(cell.x), asuint(cell.y), seed));
    h ^= h >> 15;
    h *= 0x2c1b3c6du;
    h ^= h >> 12;
    return (h & 0xFFFFFFu) / 16777215.0;
}

float NyxZoneValueNoise(float2 p, uint seed)
{
    float2 i = floor(p);
    float2 f = p - i;
    float2 u = f * f * (3.0 - 2.0 * f);
    int2 c = (int2)i;
    float a = NyxZoneHash01(c, seed);
    float b = NyxZoneHash01(c + int2(1, 0), seed);
    float d = NyxZoneHash01(c + int2(0, 1), seed);
    float e = NyxZoneHash01(c + int2(1, 1), seed);
    return lerp(lerp(a, b, u.x), lerp(d, e, u.x), u.y) * 2.0 - 1.0;
}

// Bruit fractal, 3 octaves, dans [-1, 1] (= ZoneShape.Fbm, C#).
float NyxZoneFbm(float2 p, uint seed)
{
    float sum = 0;
    float amplitude = 0.5;
    [unroll]
    for (uint o = 0; o < 3; o++)
    {
        sum += NyxZoneValueNoise(p, seed + o) * amplitude;
        amplitude *= 0.5;
        p = p * 2.03 + 17.1;
    }
    return sum / 0.875;
}

// Bord de la zone en (x, z) monde : <= 0 dedans, > 0 dehors.
//  - Sans bord naturel : -1 ou 1 (règle pair-impair, exacte : un point pile sur un bord peut compter comme dehors).
//  - Avec : distance signée au polygone (m) + amplitude × bruit.
float NyxZoneEdge(float2 position)
{
    if (any(position < _NyxZoneBounds.xy) || any(position > _NyxZoneBounds.zw))
        return 1.0;

    bool natural = _NyxZoneNatural.x > 0 || _NyxZoneNatural.z > 0;
    bool inside = false;
    float distanceSq = 3.402823e38;
    float2 a = NyxZoneVertex(_NyxZonePointCount - 1);
    for (uint i = 0; i < _NyxZonePointCount; i++)
    {
        float2 b = NyxZoneVertex(i);
        if ((b.y > position.y) != (a.y > position.y) && position.x < (a.x - b.x) * (position.y - b.y) / (a.y - b.y) + b.x)
            inside = !inside;
        if (natural)
        {
            float2 e = a - b;
            float2 w = position - b;
            float2 c = w - e * saturate(dot(w, e) / max(dot(e, e), 1e-12));
            distanceSq = min(distanceSq, dot(c, c));
        }
        a = b;
    }
    if (!natural)
        return inside ? -1.0 : 1.0;
    float edge = (inside ? -1.0 : 1.0) * sqrt(distanceSq);
    if (_NyxZoneNatural.x > 0)
        edge += _NyxZoneNatural.x * NyxZoneFbm((position - _NyxZoneOrigin.xy) * _NyxZoneNatural.y, (uint)_NyxZoneOrigin.z);
    return edge;
}

// Position monde dans le volume : hauteur, puis bord (naturel ou non). La lisière ne compte pas ici.
bool NyxInsideZone(float3 position)
{
    if (position.y < _NyxZoneHeight.x || position.y > _NyxZoneHeight.y)
        return false;
    return NyxZoneEdge(position.xz) <= 0;
}

#endif // NYX_INSTANCING_ZONE_INCLUDED
