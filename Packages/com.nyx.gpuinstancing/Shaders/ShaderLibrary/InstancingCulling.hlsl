#ifndef NYX_INSTANCING_CULLING_INCLUDED
#define NYX_INSTANCING_CULLING_INCLUDED

// Noyaux génériques de culling GPU. Le .compute qui inclut ce fichier doit déclarer :
//     #pragma kernel NyxCull
//     #pragma kernel NyxWriteArgs
// et définir AVANT l'include :
//     bool NyxEvaluateInstance(uint index, NyxInstanceData src, out NyxRenderData dst)
// qui calcule la matrice finale de l'instance (animation, flottaison...) et renvoie false si elle est morte.
//
// Par caméra et par frame :
//   NyxCull      : 1 thread par instance -> évaluation, distance, petits objets, frustum (2 yeux), LOD,
//                  puis écriture des données de rendu UNE fois dans _NyxVisible (compacté, toutes variantes),
//                  et de leur index (4 octets) dans la zone du draw (variante x LOD) de _NyxVisibleIndex.
//   NyxWriteArgs : 1 thread par draw -> nombre d'instances dans les arguments indirects, remise à zéro.
// Mémoire par jeu de buffers : visibles x 64 octets + pire cas par draw x 4 octets (au lieu de 64 octets par LOD).

#include "InstancingData.hlsl"

#define NYX_THREADS 64        // = MeshSpawner.ThreadGroupSize
#define NYX_MAX_BUCKETS 64    // = MeshSpawner.MaxBuckets

StructuredBuffer<NyxInstanceData>  _NyxInstances;
StructuredBuffer<NyxVariantInfo>   _NyxVariants;
StructuredBuffer<uint>             _NyxBucketOffsets;   // début de la zone d'index de chaque draw
RWStructuredBuffer<NyxRenderData>  _NyxVisible;         // données de rendu des instances visibles, compactées
RWStructuredBuffer<uint>           _NyxVisibleIndex;    // par draw : index dans _NyxVisible
RWStructuredBuffer<uint>           _NyxCounters;        // [draw] : instances du draw ; [_NyxBucketCount] : total visible
// Buffer de type IndirectArguments : vue DX11 typée R32_UINT, donc RWBuffer<uint> et non RWStructuredBuffer.
RWBuffer<uint>                     _NyxArgs;

uint   _NyxInstanceCount;
uint   _NyxBucketCount;
uint   _NyxGroupsX;             // groupes en X (dispatch 2D au-delà de 65 535 groupes)
uint   _NyxInstanceMultiplier;  // 2 en VR Single Pass Instanced, sinon 1
uint   _NyxPlaneSetCount;       // 1 = mono, 2 = stéréo (union des frustums des deux yeux)
float4 _NyxFrustumPlanes[12];   // normales tournées vers l'intérieur
float4 _NyxCameraPosition;      // xyz : position caméra, w : distance max²
float4 _NyxCullParams;          // x : 1 / lodBias², y : (taille écran mini / échelle de projection)²
float4 _NyxShadowSweep;         // xyz : demi-vecteur d'extrusion le long de la lumière (0 sans ombres)

groupshared uint gs_Count[NYX_MAX_BUCKETS];
groupshared uint gs_Base[NYX_MAX_BUCKETS];
groupshared uint gs_Total;
groupshared uint gs_TotalBase;

bool NyxAabbInsidePlanes(float3 center, float3 extents, uint firstPlane)
{
    [unroll]
    for (uint p = 0; p < 6; p++)
    {
        float4 plane = _NyxFrustumPlanes[firstPlane + p];
        if (dot(plane.xyz, center) + plane.w < -dot(abs(plane.xyz), extents))
            return false;
    }
    return true;
}

bool NyxSelectBucket(NyxInstanceData src, NyxRenderData dst, out uint bucket)
{
    bucket = 0;
    NyxVariantInfo variant = _NyxVariants[(uint)src.custom.w];

    // AABB monde de l'instance : centre transformé, demi-tailles = |M| x extents (ligne par ligne,
    // abs() sur une matrice n'étant pas portable d'un compilateur à l'autre)
    float3 e = variant.boundsExtents.xyz;
    float3 center = float3(dot(dst.row0.xyz, variant.boundsCenter.xyz) + dst.row0.w,
                           dot(dst.row1.xyz, variant.boundsCenter.xyz) + dst.row1.w,
                           dot(dst.row2.xyz, variant.boundsCenter.xyz) + dst.row2.w);
    float3 extents = float3(dot(abs(dst.row0.xyz), e), dot(abs(dst.row1.xyz), e), dot(abs(dst.row2.xyz), e));

    // 1. Distance (globale et par variante)
    float3 toCamera = center - _NyxCameraPosition.xyz;
    float distSq = dot(toCamera, toCamera);
    if (distSq > min(_NyxCameraPosition.w, variant.lodDistancesSq.w))
        return false;

    // 2. Petits objets : rayon projeté plus petit que la taille mini à l'écran
    if (dot(extents, extents) < distSq * _NyxCullParams.y)
        return false;

    // 3. Frustum. Avec ombres, l'AABB est étirée le long de la lumière pour garder les projeteurs hors champ.
    float3 cullCenter  = center + _NyxShadowSweep.xyz;
    float3 cullExtents = extents + abs(_NyxShadowSweep.xyz);
    bool visible = NyxAabbInsidePlanes(cullCenter, cullExtents, 0);
    if (!visible && _NyxPlaneSetCount > 1)
        visible = NyxAabbInsidePlanes(cullCenter, cullExtents, 6);
    if (!visible)
        return false;

    // 4. LOD par distance (seuils triés, les LODs absents valent +infini)
    float lodDistSq = distSq * _NyxCullParams.x;
    uint lod = (uint)(lodDistSq > variant.lodDistancesSq.x)
             + (uint)(lodDistSq > variant.lodDistancesSq.y)
             + (uint)(lodDistSq > variant.lodDistancesSq.z);
    bucket = variant.firstBucket + min(lod, variant.lodCount - 1);
    return true;
}

[numthreads(NYX_THREADS, 1, 1)]
void NyxCull(uint3 groupId : SV_GroupID, uint3 groupThreadId : SV_GroupThreadID)
{
    uint threadIndex = groupThreadId.x;

    // Compteurs du groupe à zéro
    for (uint b = threadIndex; b < _NyxBucketCount; b += NYX_THREADS)
        gs_Count[b] = 0;
    if (threadIndex == 0)
        gs_Total = 0;
    GroupMemoryBarrierWithGroupSync();

    uint instanceIndex = (groupId.y * _NyxGroupsX + groupId.x) * NYX_THREADS + threadIndex;
    bool visible = false;
    uint bucket = 0;
    uint slotInGroup = 0;
    uint dataInGroup = 0;
    NyxRenderData dst = (NyxRenderData)0;

    if (instanceIndex < _NyxInstanceCount)
    {
        NyxInstanceData src = _NyxInstances[instanceIndex];
        if (NyxEvaluateInstance(instanceIndex, src, dst))
            visible = NyxSelectBucket(src, dst, bucket);
    }

    // Réservation en 2 temps : atomique en mémoire partagée, puis 1 seule atomique globale
    // par groupe et par draw (au lieu d'une par instance) -> 64x moins de contention.
    if (visible)
    {
        InterlockedAdd(gs_Count[bucket], 1, slotInGroup);
        InterlockedAdd(gs_Total, 1, dataInGroup);
    }
    GroupMemoryBarrierWithGroupSync();

    for (uint b2 = threadIndex; b2 < _NyxBucketCount; b2 += NYX_THREADS)
    {
        uint groupCount = gs_Count[b2];
        uint groupBase = 0;
        if (groupCount > 0)
            InterlockedAdd(_NyxCounters[b2], groupCount, groupBase);
        gs_Base[b2] = groupBase;
    }
    if (threadIndex == 0)
    {
        uint totalBase = 0;
        if (gs_Total > 0)
            InterlockedAdd(_NyxCounters[_NyxBucketCount], gs_Total, totalBase);
        gs_TotalBase = totalBase;
    }
    GroupMemoryBarrierWithGroupSync();

    if (visible)
    {
        uint dataIndex = gs_TotalBase + dataInGroup;
        _NyxVisible[dataIndex] = dst;
        _NyxVisibleIndex[_NyxBucketOffsets[bucket] + gs_Base[bucket] + slotInGroup] = dataIndex;
    }
}

[numthreads(NYX_THREADS, 1, 1)]
void NyxWriteArgs(uint3 dispatchId : SV_DispatchThreadID)
{
    uint bucket = dispatchId.x;
    if (bucket >= _NyxBucketCount)
        return;

    // Arguments indirects : [0] indices par instance, [1] nombre d'instances, [2] premier indice,
    // [3] sommet de base, [4] première instance. Seul [1] change d'une frame à l'autre.
    // 2 blocs : passes caméra (x2 en Single Pass Instanced) puis passe d'ombre (jamais stéréo, donc jamais doublée).
    uint visibleCount = _NyxCounters[bucket];
    _NyxArgs[bucket * 5 + 1] = visibleCount * _NyxInstanceMultiplier;
    _NyxArgs[(_NyxBucketCount + bucket) * 5 + 1] = visibleCount;
    _NyxCounters[bucket] = 0;
    if (bucket == 0)
        _NyxCounters[_NyxBucketCount] = 0;   // total des visibles
}

#endif // NYX_INSTANCING_CULLING_INCLUDED
