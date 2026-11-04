#ifndef NYX_INSTANCING_SETUP_INCLUDED
#define NYX_INSTANCING_SETUP_INCLUDED

// Côté rendu (pipeline Built-in). Dans le shader :
//     #pragma multi_compile_instancing
//     #pragma instancing_options procedural:NyxInstancingSetup
//     #include ".../InstancingSetup.cginc"     (après UnityCG.cginc pour un shader vert/frag)
// Unity active PROCEDURAL_INSTANCING_ON pour Graphics.DrawMeshInstancedIndirect et appelle
// NyxInstancingSetup() dans UNITY_SETUP_INSTANCE_ID, avant tout calcul de position.

#include "InstancingData.hlsl"

#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
    StructuredBuffer<NyxRenderData> _NyxRenderData;    // instances visibles, compactées (toutes variantes)
    StructuredBuffer<uint> _NyxRenderIndex;            // par draw : index dans _NyxRenderData
    float _NyxBucketOffset;   // début de la zone d'index de ce draw (float : posé via MaterialPropertyBlock.SetFloat)

    NyxRenderData NyxGetRenderData()
    {
        // unity_InstanceID est déjà corrigé pour la VR Single Pass Instanced (SV_InstanceID >> 1).
        return _NyxRenderData[_NyxRenderIndex[(uint)_NyxBucketOffset + unity_InstanceID]];
    }
#endif

void NyxInstancingSetup()
{
#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
    NyxRenderData d = NyxGetRenderData();

    unity_ObjectToWorld = float4x4(d.row0, d.row1, d.row2, float4(0, 0, 0, 1));

    // Inverse affine exacte (gère l'échelle non uniforme) : adjointe / déterminant.
    // Inutilisée par le pixel shader, donc éliminée par le compilateur dans cette étape.
    float3x3 m = float3x3(d.row0.xyz, d.row1.xyz, d.row2.xyz);
    float3 c0 = cross(m[1], m[2]);
    float3 c1 = cross(m[2], m[0]);
    float3 c2 = cross(m[0], m[1]);
    float3x3 inverse3x3 = transpose(float3x3(c0, c1, c2)) * (1.0 / dot(m[0], c0));
    float3 inverseTranslation = -mul(inverse3x3, float3(d.row0.w, d.row1.w, d.row2.w));

    unity_WorldToObject = float4x4(
        float4(inverse3x3[0], inverseTranslation.x),
        float4(inverse3x3[1], inverseTranslation.y),
        float4(inverse3x3[2], inverseTranslation.z),
        float4(0, 0, 0, 1));
#endif
}

#endif // NYX_INSTANCING_SETUP_INCLUDED
