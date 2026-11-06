#ifndef NYX_INSTANCING_PLACEMENT_INCLUDED
#define NYX_INSTANCING_PLACEMENT_INCLUDED

// Placement d'un spawner, paramètres posés par GpuPlacement.cs (PlacementSetup) : candidats (grille perturbée sur la boîte
// de la zone puis amas autour des ancres, ou liste), filtre de la zone, donnée d'instance par défaut. Utilisé par
// InstancePlacement.compute (comptage, écriture des instances et des placements).

#include "InstancingData.hlsl"
#include "InstancingRandom.hlsl"
#include "InstancingVariants.hlsl"   // NyxVariantValid, _NyxDeadVariant
#include "InstancingZone.hlsl"       // NyxZoneEdge, _NyxZoneHeight, _NyxZoneNatural

// = MeshPlacement (C#), 28 octets
struct NyxPlacement
{
    float3 position;
    float  yaw;
    float  scale;
    int    variant;
    float  seed;
};

uint   _NyxPlaceFromList;           // 1 : candidats lus dans _NyxPlaceList ; 0 : grille puis amas
uint   _NyxPlaceGridCount;          // candidats [0, n) : grille ; ensuite : amas
uint   _NyxPlaceCellsX;
float4 _NyxPlaceGrid;               // xy : coin min (x, z) de la grille, zw : pas (x, z)
float4 _NyxPlaceShape;              // x : hauteur (face du haut), y : rayon des amas, z, w : échelle min, max
uint   _NyxPlaceSeed;
uint   _NyxPlaceClumps;             // amas par ancre
uint   _NyxPlaceRandomVariants;     // variantes tirées au hasard (au moins 1)
uint   _NyxPlaceTested;             // candidats [0, n) testés contre le polygone (grille d'une zone non rectangulaire)
uint   _NyxPlaceClip;               // 1 : tous les candidats testés contre le volume (polygone et hauteur)

StructuredBuffer<float4>       _NyxPlaceAnchors;   // xyz : ancres des amas
StructuredBuffer<NyxPlacement> _NyxPlaceList;

// Candidat 'index'. Procédural : mêmes tirages, dans le même ordre, que l'ancien job Burst du CPU (graine du spawner + index).
NyxPlacement NyxCandidate(uint index)
{
    NyxPlacement p;
    if (_NyxPlaceFromList != 0)
    {
        p = _NyxPlaceList[index];
        p.variant = clamp(p.variant, 0, (int)_NyxPlaceRandomVariants - 1);
        return p;
    }

    NyxRandom random = NyxCreateRandom(NyxHash2(uint2(_NyxPlaceSeed, index)) | 1u);
    if (index < _NyxPlaceGridCount)
    {
        uint x = index % _NyxPlaceCellsX;
        uint z = index / _NyxPlaceCellsX;
        float jitterX = NyxNextFloat(random);
        float jitterZ = NyxNextFloat(random);
        p.position = float3(_NyxPlaceGrid.x + ((float)x + jitterX) * _NyxPlaceGrid.z, _NyxPlaceShape.x,
                            _NyxPlaceGrid.y + ((float)z + jitterZ) * _NyxPlaceGrid.w);
    }
    else
    {
        float3 anchor = _NyxPlaceAnchors[(index - _NyxPlaceGridCount) / _NyxPlaceClumps].xyz;
        float angle = NyxNextFloat(random) * 3.14159274 * 2.0;    // math.PI en float
        float distance = _NyxPlaceShape.y * sqrt(NyxNextFloat(random));
        float s, c;
        sincos(angle, s, c);
        p.position = float3(anchor.x + c * distance, _NyxPlaceShape.x, anchor.z + s * distance);
    }
    p.yaw = NyxNextFloat(random) * 360.0;
    p.scale = _NyxPlaceShape.z + NyxNextFloat(random) * (_NyxPlaceShape.w - _NyxPlaceShape.z);   // math.lerp
    p.variant = (int)NyxNextInt(random, _NyxPlaceRandomVariants);
    p.seed = NyxNextFloat(random);
    return p;
}

// Gardé par la zone : la grille d'une zone non rectangulaire (ou à bord naturel) contre le bord ; avec le volume, tous les
// candidats contre le bord et la hauteur. Lisière : à moins de _NyxZoneNatural.z mètres du bord, un candidat sur deux,
// puis de moins en moins, est retiré (tirage propre au candidat, indépendant du placement). Placement procédural
// seulement : pour une liste (placement figé compris), _NyxZoneNatural.z vaut 0 et ses instances sont gardées.
bool NyxKeepCandidate(uint index, NyxPlacement p)
{
    bool inside = true;
    if (_NyxPlaceClip != 0)
        inside = p.position.y >= _NyxZoneHeight.x && p.position.y <= _NyxZoneHeight.y;
    if (inside && (_NyxPlaceClip != 0 || index < _NyxPlaceTested))
    {
        float edge = NyxZoneEdge(p.position.xz);
        inside = edge <= 0;
        if (inside && _NyxZoneNatural.z > 0)
        {
            uint h = NyxHash2(uint2(_NyxPlaceSeed ^ 0x9E3779B9u, index));
            inside = (h & 0xFFFFFFu) / 16777215.0 < saturate(-edge / _NyxZoneNatural.z);
        }
    }
    return inside;
}

// Donnée d'instance par défaut (= MeshSpawnerJobs.TrsInstance) : lacet + échelle + position, graine en custom.x.
// Une variante non dessinable devient la variante désactivée (jamais évaluée par le culling).
NyxInstanceData NyxPlacementInstance(NyxPlacement p)
{
    float s, c;
    sincos(p.yaw * 0.0174532925, s, c);   // math.radians
    NyxInstanceData data;
    data.row0 = float4(c * p.scale, 0, s * p.scale, p.position.x);
    data.row1 = float4(0, p.scale, 0, p.position.y);
    data.row2 = float4(-s * p.scale, 0, c * p.scale, p.position.z);
    data.custom = float4(p.seed, 0, 0, NyxVariantValid((uint)p.variant) ? (float)p.variant : (float)_NyxDeadVariant);
    return data;
}

#endif // NYX_INSTANCING_PLACEMENT_INCLUDED
